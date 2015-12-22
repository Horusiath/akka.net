using System;
using System.Threading.Tasks;
using Akka.Actor;

namespace Akka.Eventsourced.CRDT
{

    /// <summary>
    /// Replicated counter CRDT service.
    /// </summary>
    public class CounterService : CRDTService<Counter, long>
    {
        private readonly string serviceId;
        private readonly IActorRef eventLog;
        private readonly ICRDTServiceOperations<Counter, long> operations;

        public CounterService(string serviceId, IActorRef eventLog, ActorSystem system, ICRDTServiceOperations<Counter, long> operations = null)
        {
            this.serviceId = serviceId;
            this.eventLog = eventLog;
            this.operations = operations ?? Counter.Operations;

            Start(system);
        }

        public override string ServiceId { get { return serviceId; } }
        public override IActorRef EventLog { get { return eventLog; } }
        public override ICRDTServiceOperations<Counter, long> Operations { get { return operations; } }

        public Task<long> UpdateAsync(string crdtId, long delta)
        {
            return UpdateOperationAsync(crdtId, new UpdateOp(delta));
        }
    }

    /// <summary>
    /// A replicated counter.
    /// </summary>
    [Serializable]
    public struct Counter : IValued<long>
    {
        public static readonly ICRDTServiceOperations<Counter, long> Operations = CounterServiceOperations.Instance;

        private readonly long value;

        internal Counter(long value = 0L)
        {
            this.value = value;
        }

        public long Value { get { return value; } }

        /// <summary>
        /// Adds <paramref name="delta"/> to the counter's <see cref="Value"/>, returning updated counter in result.
        /// </summary>
        public Counter Update(long delta)
        {
            return new Counter(Value + delta);
        }
    }

    [Serializable]
    internal struct UpdateOp
    {
        public readonly long Delta;

        public UpdateOp(long delta)
        {
            Delta = delta;
        }
    }

    internal sealed class CounterServiceOperations : ICRDTServiceOperations<Counter, long>
    {
        public static readonly Counter EmptyCounter = new Counter(0L);
        public static readonly CounterServiceOperations Instance = new CounterServiceOperations();

        private CounterServiceOperations()
        {
        }

        public bool CheckPreconditions { get { return false; } }

        public Counter Default { get { return EmptyCounter; } }

        public long GetValue(Counter crdt)
        {
            return crdt.Value;
        }

        public object Updating(Counter crdt, object operation)
        {
            return operation;
        }

        public Counter Updated(Counter crdt, object operation, DurableEvent e)
        {
            if (operation is UpdateOp)
            {
                return crdt.Update(((UpdateOp) operation).Delta);
            }
            return crdt;
        }
    }
}