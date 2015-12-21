using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;

namespace Akka.Eventsourced.CRDT
{
    /// <summary>
    /// Replicated <see cref="MultiValueRegister{T}"/> service.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class MultiValueRegisterService<T> : CRDTService<MultiValueRegister<T>, IImmutableSet<T>>
    {
        private readonly string serviceId;
        private readonly IActorRef eventLog;
        private readonly ICRDTServiceOperations<MultiValueRegister<T>, IImmutableSet<T>> operations;

        public MultiValueRegisterService(string serviceId, IActorRef eventLog, ActorSystem system, ICRDTServiceOperations<MultiValueRegister<T>, IImmutableSet<T>> operations = null)
        {
            this.serviceId = serviceId;
            this.eventLog = eventLog;
            this.operations = operations ?? MultiValueRegister<T>.Operations;

            Start(system);
        }

        public override string ServiceId { get { return serviceId; } }
        public override IActorRef EventLog { get { return eventLog; } }
        public override ICRDTServiceOperations<MultiValueRegister<T>, IImmutableSet<T>> Operations { get { return operations; } }

        public Task<IImmutableSet<T>> SetValueAsync(string crdtId, T value)
        {
            return UpdateOperationAsync(crdtId, new SetOp<T>(value));
        }
    }
    /// <summary>
    /// Replicated Multi-Value Register.
    /// </summary>
    public sealed class MultiValueRegister<T> : IValued<IEnumerable<T>>, ISerializableCRDT
    {
        public static readonly ICRDTServiceOperations<MultiValueRegister<T>, IImmutableSet<T>> Operations = MultiValueRegisterOperations<T>.Instance;

        internal IImmutableSet<Registered<T>> registered;

        public MultiValueRegister(IImmutableSet<Registered<T>> registered = null)
        {
            this.registered = registered ?? ImmutableHashSet<Registered<T>>.Empty;
        }

        public IEnumerable<T> Value { get { return registered.Select(x => x.Value); } }

        public MultiValueRegister<T> Set(T value, VectorTime vectorTime, long systemTime = 0L, string emmitterId = null)
        {
            var wasUpdated = false;
            var set = ImmutableHashSet<Registered<T>>.Empty;

            foreach (var register in registered)
            {
                if (vectorTime > register.UpdateTimestamp)
                {
                    set = set.Add(register.WithValue(value, vectorTime));
                    wasUpdated = true;
                }
                else set = set.Add(register);
            }

            return wasUpdated
                ? new MultiValueRegister<T>(set)
                : new MultiValueRegister<T>(registered.Add(new Registered<T>(value, vectorTime, systemTime, emmitterId ?? string.Empty)));
        }
    }

    /// <summary>
    /// Internal representation of <see cref="MultiValueRegister"/> value.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public struct Registered<T> : IEquatable<Registered<T>>
    {
        public readonly T Value;
        public readonly VectorTime UpdateTimestamp;
        public readonly long SystemTimestamp;
        public readonly String EmitterId;

        public Registered(T value, VectorTime updateTimestamp, long systemTimestamp, string emitterId)
        {
            Value = value;
            UpdateTimestamp = updateTimestamp;
            SystemTimestamp = systemTimestamp;
            EmitterId = emitterId ?? string.Empty;
        }

        public Registered<T> WithValue(T value, VectorTime vectorTime)
        {
            return new Registered<T>(value, vectorTime, SystemTimestamp, EmitterId);
        }

        public bool Equals(Registered<T> other)
        {
            return Equals(SystemTimestamp, other.SystemTimestamp)
                   && Equals(EmitterId, other.EmitterId)
                   && Equals(UpdateTimestamp, other.UpdateTimestamp)
                   && Equals(Value, other.Value);
        }

        public override bool Equals(object obj)
        {
            if (obj is Registered<T>) return Equals((Registered<T>) obj);
            return false;
        }
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = EqualityComparer<T>.Default.GetHashCode(Value);
                hashCode = (hashCode * 397) ^ UpdateTimestamp.GetHashCode();
                hashCode = (hashCode * 397) ^ SystemTimestamp.GetHashCode();
                hashCode = (hashCode * 397) ^ EmitterId.GetHashCode();
                return hashCode;
            }
        }
    }

    internal struct SetOp<T>
    {
        public readonly T Value;

        public SetOp(T value)
        {
            Value = value;
        }
    }

    internal sealed class MultiValueRegisterOperations<T> : ICRDTServiceOperations<MultiValueRegister<T>, IImmutableSet<T>>
    {
        public static readonly MultiValueRegisterOperations<T> Instance = new MultiValueRegisterOperations<T>();
        private static readonly MultiValueRegister<T> EmptyRegister = new MultiValueRegister<T>();

        private MultiValueRegisterOperations()
        {
        }

        public bool CheckPreconditions { get { return false; } }

        public MultiValueRegister<T> Default { get { return EmptyRegister; } }

        public IImmutableSet<T> GetValue(MultiValueRegister<T> crdt)
        {
            return crdt.Value.ToImmutableHashSet();
        }

        public object Updating(MultiValueRegister<T> crdt, object operation)
        {
            return operation;
        }

        public MultiValueRegister<T> Updated(MultiValueRegister<T> crdt, object operation, DurableEvent e)
        {
            if (operation is SetOp<T>)
                return crdt.Set(((SetOp<T>)operation).Value, e.VectorTimestamp, e.SystemTimestamp, e.EmitterId);

            return crdt;
        }
    }
}