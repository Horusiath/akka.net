using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;

namespace Akka.Eventsourced.Logs
{

    [Serializable]
    public sealed class EventLogSettings
    {

        public static EventLogSettings Create(ActorSystem system)
        {
            return Create(system.Settings.Config.GetConfig("akka.eventsourced.log"));
        }

        private static EventLogSettings Create(Config config)
        {
            return new EventLogSettings(
                maxPartitionSize: config.GetInt("max-partition-size"),
                maxRetries: config.GetInt("max-retries"),
                retryDelay: config.GetTimeSpan("retry-delay"),
                deletionRetryDelay: config.GetTimeSpan("deletion-retry-delay"));
        }

        /// <summary>
        /// Maximum number of events to store per partition. If storage backend doesn't 
        /// support partitioning, this value should be <see cref="long.MaxValue"/>.
        /// </summary>
        public readonly long MaxPartitionSize;

        /// <summary>
        /// Maximum number of clock recovery retries.
        /// </summary>
        public readonly int MaxRetries;

        /// <summary>
        /// Delay between clock recovery retries.
        /// </summary>
        public readonly TimeSpan RetryDelay;

        /// <summary>
        /// Delay between two retries to delete all events while keeping those that are not yet replicated.
        /// </summary>
        public readonly TimeSpan DeletionRetryDelay;

        public EventLogSettings(long maxPartitionSize, int maxRetries, TimeSpan retryDelay, TimeSpan deletionRetryDelay)
        {
            Contract.Requires(maxPartitionSize > 0);
            Contract.Requires(maxRetries > 0);

            MaxPartitionSize = maxPartitionSize;
            MaxRetries = maxRetries;
            RetryDelay = retryDelay;
            DeletionRetryDelay = deletionRetryDelay;
        }
    }
    
    /// <summary>
    /// Clock that tracks current version number and sequence number of target event log.
    /// </summary>
    [Serializable]
    public struct EventLogClock
    {
        public static readonly EventLogClock Zero = new EventLogClock(0L, VectorTime.Zero);

        public readonly long SequenceNr;
        public readonly VectorTime VersionVector;

        public EventLogClock(long sequenceNr, VectorTime versionVector)
        {
            SequenceNr = sequenceNr;
            VersionVector = versionVector;
        }

        public EventLogClock AdvanceSequenceNr(long delta = 1L)
        {
            return new EventLogClock(SequenceNr + delta, VersionVector);
        }

        public EventLogClock Update(DurableEvent e)
        {
            return new EventLogClock(e.LocalSequenceNr, VersionVector.Merge(e.VectorTimestamp));
        }
    }

    [Serializable]
    public struct BatchReadResult : IDurableEventBatch
    {
        public readonly long To;

        public BatchReadResult(IEnumerable<DurableEvent> events, long to)
        {
            Events = events;
            To = to;
        }

        public int Count { get { return Events.Count(); } }
        public IEnumerable<DurableEvent> Events { get; private set; }
    }

    [Serializable]
    public struct DeletionMetadata
    {
        public readonly long ToSequenceNr;
        public readonly IImmutableSet<string> RemoteLogIds;

        public DeletionMetadata(long toSequenceNr, IImmutableSet<string> remoteLogIds)
        {
            ToSequenceNr = toSequenceNr;
            RemoteLogIds = remoteLogIds;
        }
    }

    /// <summary>
    /// Abstract event log able to handle both eventsourcing and replication protocol messages.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract partial class EventLog<T> : ActorBase, IWithUnboundedStash, IEventLogStorageProvider<T>
    {
        public readonly string Id;

        private EventLogClock clock = EventLogClock.Zero;
        private DeletionMetadata deletionMetadata = new DeletionMetadata(0, ImmutableHashSet<string>.Empty);
        private bool isDeletionInProgress = false;
        private IImmutableDictionary<string, long> remoteReplicationProgress = ImmutableDictionary<string, long>.Empty;
        private IImmutableDictionary<string, VectorTime> replicaVersionVectors = ImmutableDictionary<string, VectorTime>.Empty;
        private SubscriberRegistry registry = SubscriberRegistry.Empty;
        private IActorRef channel = null;
        private ILoggingAdapter log = Context.GetLogger();
        private EventLogSettings settings = EventLogSettings.Create(Context.System);

        protected EventLog(string id)
        {
            Id = id;
        }


        public IStash Stash { get; set; }
        public EventLogSettings Settings { get { return settings; } }
        public long CurrentSystemTime { get { return DateTime.UtcNow.Ticks; } }

        protected override bool Receive(object message)
        {
            return Initializing(message);
        }

        private bool Initializing(object message)
        {
            throw new NotImplementedException();
        }

        private bool Initialized(object message)
        {
            throw new NotImplementedException();
        }

        public virtual Task<EventLogClock> RecoverClockAsync()
        {
            throw new NotImplementedException();
        }

        public virtual void RecoverClockSuccess(EventLogClock clock) { }

        public virtual void RecoverClockFailure(Exception cause) { }

        public virtual Task<IImmutableDictionary<string, long>> ReadReplicationProgressesAsync()
        {
            throw new NotImplementedException();
        }

        public virtual Task<long> ReadReplicationProgressAsync(string eventLogId)
        {
            throw new NotImplementedException();
        }

        public virtual Task WriteReplicationProgressAsync(string eventLogId, long progress)
        {
            throw new NotImplementedException();
        }

        public virtual IEnumerable<DurableEvent> CreateEventIterator(T parameters)
        {
            throw new NotImplementedException();
        }

        public virtual T EventIteratorParameters(long fromSequenceNr, long toSequenceNr)
        {
            throw new NotImplementedException();
        }

        public virtual T EventIteratorParameters(long fromSequenceNr, long toSequenceNr, string aggregateId)
        {
            throw new NotImplementedException();
        }

        public virtual Task<BatchReadResult> ReadAsync(long fromSequenceNr, long toSequenceNr, int max, Func<DurableEvent, bool> filter)
        {
            throw new NotImplementedException();
        }

        public virtual Task WriteAsync(IEnumerable<DurableEvent> events, long partition, EventLogClock clock)
        {
            throw new NotImplementedException();
        }

        public virtual void WriteDeletionMetadata(DeletionMetadata metadata)
        {
            throw new NotImplementedException();
        }

        public virtual Task DeleteAsync(long sequenceNr)
        {
            var source = new TaskCompletionSource<object>();
            source.SetException(new DeletionNotSupportedException());
            return source.Task;
        }

        public virtual Task<DeletionMetadata> ReadDeletionMetadataAsync()
        {
            throw new NotImplementedException();
        }

        public static long PartitionOf(long sequenceNr, long maxPartitionSize)
        {
            return sequenceNr == 0L ? -1L : (sequenceNr - 1)/maxPartitionSize;
        }

        public static long RemainingPartitionSize(long sequenceNr, long maxPartitionSize)
        {
            var m = sequenceNr & maxPartitionSize;
            return m == 0L ? m : maxPartitionSize - m;
        }

        public static long FirstSequenceNr(long partition, long maxPartitionSize)
        {
            return partition*maxPartitionSize + 1L;
        }

        public static long LastSequenceNr(long partition, long maxPartitionSize)
        {
            return (partition + 1L)*maxPartitionSize;
        }

        protected override void PreStart()
        {
            //TODO
        }

        private EventLogClock AdjustSequenceNr(long batchSize, long maxBatchSize, EventLogClock clock, out long currentPartition)
        {
            Contract.Requires(batchSize <= maxBatchSize);

            currentPartition = PartitionOf(clock.SequenceNr, maxBatchSize);
            var remainingSize = RemainingPartitionSize(clock.SequenceNr, maxBatchSize);
            if (remainingSize < batchSize)
            {
                currentPartition += 1L;
                return clock.AdvanceSequenceNr(remainingSize);
            }
            else return clock;
        }

        private long AdjustFromSequenceNr(long sequenceNr)
        {
            return Math.Max(sequenceNr, deletionMetadata.ToSequenceNr + 1L);
        }

        private IActorRef Replayer(IActorRef requestor, Func<IEnumerator<DurableEvent>> enumeratorFactory, long fromSequenceNr)
        {
            return Context.ActorOf(Props.Create(() => new ChunkedEventReplay(requestor, enumeratorFactory)));
        }
    }
}