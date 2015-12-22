using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Akka.Eventsourced.Logs
{
    public interface IEventLogStorageProvider<T>
    {
        /// <summary>
        /// Event log settings.
        /// </summary>
        EventLogSettings Settings { get; }

        /// <summary>
        /// Asynchronously recovers the <see cref="EventLogClock"/> during initialization.
        /// </summary>
        Task<EventLogClock> RecoverClockAsync();

        /// <summary>
        /// Called after successfull event log clock recovery.
        /// </summary>
        void RecoverClockSuccess(EventLogClock clock);

        /// <summary>
        /// Called after failed event log clock recovery.
        /// </summary>
        /// <param name="cause"></param>
        void RecoverClockFailure(Exception cause);

        /// <summary>
        /// Asynchronously reads all stored local replication progresses.
        /// </summary>
        Task<IImmutableDictionary<string, long>> ReadReplicationProgressesAsync();

        /// <summary>
        /// Asynchronously gets the replication progress for given <see cref="eventLogId"/>.
        /// </summary>
        Task<long> ReadReplicationProgressAsync(string eventLogId);

        /// <summary>
        /// Asynchronously writes the replication <paramref name="progress"/> for given <paramref name="eventLogId"/>.
        /// </summary>
        Task WriteReplicationProgressAsync(string eventLogId, long progress);

        /// <summary>
        /// 
        /// </summary>
        IEnumerable<DurableEvent> CreateEventIterator(T parameters);

        /// <summary>
        /// Creates an encoded event iterator parameters object, passed later to <see cref="CreateEventIterator"/>.
        /// </summary>
        T EventIteratorParameters(long fromSequenceNr, long toSequenceNr);

        /// <summary>
        /// Creates an encoded event iterator parameters object, passed later to <see cref="CreateEventIterator"/>.
        /// </summary>
        T EventIteratorParameters(long fromSequenceNr, long toSequenceNr, string aggregateId);

        /// <summary>
        /// Asynchronously reads batch of events from the event log.
        /// </summary>s
        Task<BatchReadResult> ReadAsync(long fromSequenceNr, long toSequenceNr, int max, Func<DurableEvent, bool> filter);

        /// <summary>
        /// Asynchronously writes <paramref name="events"/> to the given <paramref name="partition"/>.
        /// </summary>
        Task WriteAsync(IEnumerable<DurableEvent> events, long partition, EventLogClock clock);

        /// <summary>
        /// Store metadata regarding a delete request.
        /// </summary>
        void WriteDeletionMetadata(DeletionMetadata metadata);

        /// <summary>
        /// Asynchronously starts a physical event deletion procedure.
        /// </summary>
        /// <exception cref="DeletionNotSupportedException">Thrown if event log doesn't support physical events deletion.</exception>
        Task DeleteAsync(long sequenceNr);

        /// <summary>
        /// Returns current <see cref="DeletionMetadata"/>.
        /// </summary>
        Task<DeletionMetadata> ReadDeletionMetadataAsync();
    }

    [Serializable]
    public class DeletionNotSupportedException : NotSupportedException
    {
        public DeletionNotSupportedException()
        {
        }

        protected DeletionNotSupportedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}