using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;

namespace Akka.Eventsourced.CRDT
{
    /// <summary>
    /// Replicated <see cref="OrSet{T}"/> CRDT service.
    /// </summary>
    public class OrSetService<T> : CRDTService<OrSet<T>, IImmutableSet<T>>
    {
        private readonly string serviceId;
        private readonly IActorRef eventLog;
        private readonly ICRDTServiceOperations<OrSet<T>, IImmutableSet<T>> operations;

        public OrSetService(string serviceId, IActorRef eventLog, ActorSystem system, ICRDTServiceOperations<OrSet<T>, IImmutableSet<T>> operations = null)
        {
            this.serviceId = serviceId;
            this.eventLog = eventLog;
            this.operations = operations ?? OrSet<T>.Operations;

            Start(system);
        }

        public override string ServiceId { get { return serviceId; } }
        public override IActorRef EventLog { get { return eventLog; } }
        public override ICRDTServiceOperations<OrSet<T>, IImmutableSet<T>> Operations { get { return operations; } }

        public Task<IImmutableSet<T>> AddAsync(string crdtId, T value)
        {
            return UpdateOperationAsync(crdtId, new AddOp<T>(value));
        }

        public Task<IImmutableSet<T>> RemoveAsync(string crdtId, T value)
        {
            return UpdateOperationAsync(crdtId, new RemoveOp<T>(value));
        }
    }

    /// <summary>
    /// Replicated OR-Set. In case of concurrent <see cref="Add"/>/<see cref="Remove"/> operations, <see cref="Add"/> has precedence. 
    /// </summary>
    public class OrSet<T> : IValued<IEnumerable<T>>, ISerializableCRDT
    {
        public static ICRDTServiceOperations<OrSet<T>, IImmutableSet<T>> Operations = OrSetOperations<T>.Instance;

        private IImmutableSet<Versioned<T>> versionedEntries;

        public OrSet(IImmutableSet<Versioned<T>> versionedEntries = null)
        {
            this.versionedEntries = versionedEntries ?? ImmutableHashSet<Versioned<T>>.Empty;
        }

        public IEnumerable<T> Value { get { return versionedEntries.Select(x => x.Value); } }

        /// <summary>
        /// Adds an entry and returns updated <see cref="OrSet{T}"/>.
        /// </summary>
        public OrSet<T> Add(T entry, VectorTime timestamp)
        {
            return new OrSet<T>(versionedEntries.Add(new Versioned<T>(entry, timestamp)));
        }

        /// <summary>
        /// Collects all timestamps for a given <paramref name="entry"/>.
        /// </summary>
        public IImmutableSet<VectorTime> PrepareRemove(T entry)
        {
            return versionedEntries
                .Where(v => Equals(v.Value, entry))
                .Select(v => v.UpdateTimestamp).ToImmutableHashSet();
        }

        /// <summary>
        /// Removes entries matching <paramref name="entry"/> and <paramref name="timestamps"/> and returns updated <see cref="OrSet{T}"/>.
        /// </summary>
        public OrSet<T> Remove(T entry, IImmutableSet<VectorTime> timestamps)
        {
            return new OrSet<T>(versionedEntries.Except(timestamps.Select(t => new Versioned<T>(entry, t))));
        }
    }

    internal class OrSetOperations<T> : ICRDTServiceOperations<OrSet<T>, IImmutableSet<T>>
    {
        public static readonly OrSetOperations<T> Instance = new OrSetOperations<T>();
        private static readonly OrSet<T> EmptySet = new OrSet<T>();

        private OrSetOperations() { }

        public bool CheckPreconditions { get { return true; } }

        public OrSet<T> Default { get { return EmptySet; } }

        public IImmutableSet<T> GetValue(OrSet<T> crdt)
        {
            return crdt.Value.ToImmutableHashSet();
        }

        public object Updating(OrSet<T> crdt, object operation)
        {
            if (operation is RemoveOp<T>)
            {
                var op = (RemoveOp<T>) operation;
                var timestamps = crdt.PrepareRemove(op.Entry);

                if (timestamps.Count != 0)
                    return new RemoveOp<T>(op.Entry, timestamps);

                return null;
            }

            return operation;
        }

        public OrSet<T> Updated(OrSet<T> crdt, object operation, DurableEvent e)
        {
            if (operation is RemoveOp<T>)
            {
                var remove = (RemoveOp<T>) operation;
                return crdt.Remove(remove.Entry, remove.Timestamps);
            }

            if (operation is AddOp<T>)
            {
                var add = (AddOp<T>)operation;
                return crdt.Add(add.Entry, e.VectorTimestamp);
            }

            return crdt;
        }
    }

    internal struct AddOp<T>
    {
        public readonly T Entry;

        public AddOp(T entry)
        {
            Entry = entry;
        }
    }

    internal struct RemoveOp<T>
    {
        public readonly T Entry;
        public readonly IImmutableSet<VectorTime> Timestamps;

        public RemoveOp(T entry, IImmutableSet<VectorTime> timestamps = null)
        {
            Entry = entry;
            Timestamps = timestamps ?? ImmutableHashSet<VectorTime>.Empty;
        }
    }
}