using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;

namespace Akka.Eventsourced.CRDT
{
    /// <summary>
    /// Replicated <see cref="LastWriteWinsRegister{T}"/> CRDT service.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class LastWriteWinsRegisterService<T> : CRDTService<LastWriteWinsRegister<T>, T>
    {
        private readonly string serviceId;
        private readonly IActorRef eventLog;
        private readonly ICRDTServiceOperations<LastWriteWinsRegister<T>, T> operations;

        public LastWriteWinsRegisterService(string serviceId, IActorRef eventLog, ActorSystem system, ICRDTServiceOperations<LastWriteWinsRegister<T>, T> operations = null)
        {
            this.serviceId = serviceId;
            this.eventLog = eventLog;
            this.operations = operations ?? LastWriteWinsRegister<T>.Operations;
        }

        public override string ServiceId { get { return serviceId; } }
        public override IActorRef EventLog { get { return eventLog; } }
        public override ICRDTServiceOperations<LastWriteWinsRegister<T>, T> Operations { get { return operations; } }

        /// <summary>
        /// Assigns <paramref name="value"/> to Last-Write-Wins register and returns updated value.
        /// </summary>
        public Task<T> SetValueAsync(string crdtId, T value)
        {
            return UpdateOperationAsync(crdtId, new SetOp<T>(value));
        }
    }
    
    /// <summary>
    /// Replicated Last Write Wins Register. In case of the multiple concurrent assigments,
    /// the last write request always wins. Last write is determined based on vector timestamp 
    /// or system timestamp.
    /// </summary>
    /// <remarks>
    /// This implementation relies on synchronized system clocks.
    /// </remarks>
    public class LastWriteWinsRegister<T> : IValued<T>, ISerializableCRDT
    {
        public static readonly ICRDTServiceOperations<LastWriteWinsRegister<T>, T> Operations = LastWriteWinsRegisterOperations<T>.Instance;

        private MultiValueRegister<T> register;

        public LastWriteWinsRegister(MultiValueRegister<T> register = null)
        {
            this.register = register ?? MultiValueRegister<T>.Operations.Default;
        }

        public T Value
        {
            get
            {
                return register.registered.OrderByDescending(x => x, LastWriteWinsComparer<T>.Instance).FirstOrDefault().Value;
            }
        }

        public LastWriteWinsRegister<T> SetValue(T value, VectorTime vectorTime, long systemTime = 0L, string emitterId = null)
        {
            return new LastWriteWinsRegister<T>(register.Set(value, vectorTime, systemTime, emitterId));
        }
    }

    internal sealed class LastWriteWinsComparer<T> : IComparer<Registered<T>>
    {
        public static readonly IComparer<Registered<T>> Instance = new LastWriteWinsComparer<T>();
        private LastWriteWinsComparer() { }  

        public int Compare(Registered<T> x, Registered<T> y)
        {
            if (x.SystemTimestamp == y.SystemTimestamp)
                return x.EmitterId.CompareTo(y.EmitterId);
            else
                return x.SystemTimestamp.CompareTo(y.SystemTimestamp);
        }
    }

    internal sealed class LastWriteWinsRegisterOperations<T> : ICRDTServiceOperations<LastWriteWinsRegister<T>, T>
    {
        public static readonly LastWriteWinsRegisterOperations<T> Instance = new LastWriteWinsRegisterOperations<T>();
        private static readonly LastWriteWinsRegister<T> EmptyRegister = new LastWriteWinsRegister<T>();

        private LastWriteWinsRegisterOperations() { }
        
        public bool CheckPreconditions { get { return false; } }

        public LastWriteWinsRegister<T> Default { get { return EmptyRegister; } }

        public T GetValue(LastWriteWinsRegister<T> crdt)
        {
            return crdt.Value;
        }

        public object Updating(LastWriteWinsRegister<T> crdt, object operation)
        {
            return operation;
        }

        public LastWriteWinsRegister<T> Updated(LastWriteWinsRegister<T> crdt, object operation, DurableEvent e)
        {
            if (operation is SetOp<T>)
            {
                var value = ((SetOp<T>) operation).Value;
                return crdt.SetValue(value, e.VectorTimestamp, e.SystemTimestamp, e.EmitterId);
            }

            return crdt;
        }
    }
}