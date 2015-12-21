using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Util.Internal.Collections;

namespace Akka.Eventsourced.CRDT
{
    /// <summary>
    /// Interface to be implemented by the CRDTs, if they want to be managed by <see cref="CRDTService"/>
    /// </summary>
    /// <typeparam name="TCrdt">CRDT type</typeparam>
    /// <typeparam name="TValue">Type of a value stored</typeparam>
    public interface ICRDTServiceOperations<TCrdt, out TValue>
    {
        /// <summary>
        /// If true, CRDT will check the preconditions. Setting this value to false may improve performance.
        /// </summary>
        bool CheckPreconditions { get; }

        /// <summary>
        /// Gets the default instance of the CRDT structure.
        /// </summary>
        TCrdt Default { get; }

        /// <summary>
        /// Returns CRDT value.
        /// </summary>
        TValue GetValue(TCrdt crdt);

        /// <summary>
        /// First phase of the updating operation.
        /// </summary>
        object Updating(TCrdt crdt, object operation);

        /// <summary>
        /// Second phase of the updating operation.
        /// </summary>
        TCrdt Updated(TCrdt crdt, object operation, DurableEvent e);
    }

    /// <summary>
    /// CRDT interface for retrieval of current CDTS's structure value;
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IValued<out T>
    {
        T Value { get; }
    }

    /// <summary>
    /// Persistent event containing update operation.
    /// </summary>
    public struct ValueUpdated
    {
        /// <summary>
        /// Identifier of a CRDT structure.
        /// </summary>
        public readonly string CrdtId;

        /// <summary>
        /// Update operation.
        /// </summary>
        public readonly object Operation;

        public ValueUpdated(string crdtId, object operation)
        {
            CrdtId = crdtId;
            Operation = operation;
        }
    }

    [Serializable]
    public class ServiceNotStartedException : Exception
    {
        public ServiceNotStartedException() { }
        public ServiceNotStartedException(string message) : base(message) { }
        protected ServiceNotStartedException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
    }

    /// <summary>
    /// Generic, replicated CRDT service used for managing a map of CRDT structures identified by name string.
    /// Replication is based on event log preserving casual events ordering.
    /// </summary>
    /// <typeparam name="TCrdt">CRDT type</typeparam>
    /// <typeparam name="TValue">Type of a value stored</typeparam>
    public abstract class CRDTService<TCrdt, TValue> : IDisposable
    {
        public abstract string ServiceId { get; }
        public abstract IActorRef EventLog { get; }
        public abstract ICRDTServiceOperations<TCrdt, TValue> Operations { get; }

        private ActorSystem system = null;
        private IActorRef manager = null;

        public bool IsStarted { get { return manager != null; } }

        /// <summary>
        /// Starts a <see cref="CRDTService{TCrdt,TValue}"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if starting service has been started already.</exception>
        public void Start(ActorSystem system)
        {
            if (!IsStarted)
            {
                var opsType = Operations.Default.GetType();
                var aggregateId = Uri.EscapeDataString(opsType.Name);

                this.system = system;
                this.manager = system.ActorOf(CRDTManager<TCrdt, TValue>.Props(this));
            }
            else throw new InvalidOperationException(string.Format("Couldn't start a [{0}] service. It's started already.", GetType()));
        }

        /// <summary>
        /// Stops a <see cref="CRDTService{TCrdt,TValue}"/>, if it was started.
        /// </summary>
        public void Stop()
        {
            if (IsStarted)
            {
                system.Stop(manager);
                system = null;
                manager = null;
            }
        }

        /// <summary>
        /// Stops a <see cref="CRDTService{TCrdt,TValue}"/>, if it was started.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Asynchronously returns the value of the CRDT identified by <paramref name="crdtId"/>.
        /// </summary>
        /// <param name="crdtId">Identifier of the CRDT structure.</param>
        /// <returns></returns>
        public Task<TValue> GetValueAsync(string crdtId)
        {
            if (!IsStarted)
                return ServiceNotStartedFailure<TValue>();
            else
                return manager.Ask<GetReply<TValue>>(new Get(crdtId)).ContinueWith(t => t.Result.Value);
        }

        /// <summary>
        /// Asynchronously saves a snapshot of the CRDT identified by <paramref name="crdtId"/>.
        /// </summary>
        /// <param name="crdtId"></param>
        /// <returns></returns>
        public Task<SnapshotMetadata> SaveValueAsync(string crdtId)
        {
            if (!IsStarted)
                return ServiceNotStartedFailure<SnapshotMetadata>();
            else
                return manager.Ask<SaveReply>(new Save(crdtId)).ContinueWith(t => t.Result.Metadata);
        }

        /// <summary>
        /// Asynchronously updates the CRDT structure identified by <paramref name="crdtId"/> with given <paramref name="operation"/>.
        /// </summary>
        /// <param name="crdtId"></param>
        /// <param name="operation"></param>
        /// <returns>Updated value of the CRDT</returns>
        public Task<TValue> UpdateOperationAsync(string crdtId, object operation)
        {
            if (!IsStarted)
                return ServiceNotStartedFailure<TValue>();
            else
                return manager.Ask<UpdateReply<TValue>>(new Update(crdtId, operation)).ContinueWith(t => t.Result.Value);

        }

        private Task<TResult> ServiceNotStartedFailure<TResult>()
        {
            var promise = new TaskCompletionSource<TResult>();
            promise.SetException(
                new ServiceNotStartedException(string.Format("Cannot get CRDT value, because service [{0}] was not started", GetType())));
            return promise.Task;
        }
    }

    internal sealed class CRDTActor<TCrdt, TValue> : EventsourcedActor
    {
        public static Props Props(CRDTService<TCrdt, TValue> service, string crdtId)
        {
            return Actor.Props.Create(() => new CRDTActor<TCrdt, TValue>(service, crdtId)).WithDeploy(Deploy.Local);
        }

        private readonly string id;
        private readonly string aggregateId;
        private readonly IActorRef eventLog;
        private readonly ICRDTServiceOperations<TCrdt, TValue> ops;
        private readonly string crdtId;
        private TCrdt crdt;

        private CRDTActor(CRDTService<TCrdt, TValue> service, string crdtId)
        {
            this.crdtId = crdtId;
            ops = service.Operations;
            crdt = ops.Default;
            eventLog = service.EventLog;
            id = service.ServiceId + "_" + crdtId;
            aggregateId = Uri.EscapeDataString(crdt.GetType().Name) + "_" + crdtId;
        }

        public override string Id { get { return id; } }
        public override string AggregateId { get { return aggregateId; } }
        public override IActorRef EventLog { get { return eventLog; } }
        public override bool IsStateSync { get { return ops.CheckPreconditions; } }

        public override bool OnCommand(object message)
        {
            if (message is ICrdtIdentified && ((ICrdtIdentified) message).CrdtId == crdtId)
            {
                return message.Match()
                    .With<Get>(get => Sender.Tell(new GetReply<TValue>(crdtId, ops.GetValue(crdt))))
                    .With<Update>(update =>
                    {
                        var op = ops.Updating(crdt, update.Operation);
                        if (op == null)
                            Sender.Tell(new UpdateReply<TValue>(crdtId, ops.GetValue(crdt)));
                        else
                        {
                            var sender = Sender;
                            Persist(new ValueUpdated(crdtId, op), result =>
                            {
                                var reply = result.IsSuccess
                                    ? (object)new UpdateReply<TValue>(crdtId, ops.GetValue(crdt))
                                    : new Status.Failure(result.Exception);
                                sender.Tell(reply);
                            });
                        }
                    })
                    .With<Save>(save =>
                    {
                        var sender = Sender;
                        Save(crdt, result =>
                        {
                            var reply = result.IsSuccess
                                ? (object)new SaveReply(crdtId, result.Value)
                                : new Status.Failure(result.Exception);
                            sender.Tell(reply);
                        });
                    })
                    .WasHandled;
            }

            return false;
        }

        public override bool OnEvent(object message)
        {
            if (message is ValueUpdated)
            {
                crdt = ops.Updated(crdt, ((ValueUpdated)message).Operation, LastHandledEvent);
                return true;
            }

            return false;
        }

        public override bool OnSnapshot(object message)
        {
            if (message is TCrdt)
            {
                crdt = (TCrdt)message;
                return true;
            }

            return false;
        }
    }

    internal sealed class CRDTManager<TCrdt, TValue> : ReceiveActor
    {
        public static Props Props(CRDTService<TCrdt, TValue> service)
        {
            return Actor.Props.Create(() => new CRDTManager<TCrdt, TValue>(service)).WithDeploy(Deploy.Local);
        }

        public CRDTManager(CRDTService<TCrdt, TValue> service)
        {
            var crdts = ImmutableDictionary<string, IActorRef>.Empty;
            
            Receive<ICrdtIdentified>(identified =>
            {
                IActorRef crdtRef;
                if (!crdts.TryGetValue(identified.CrdtId, out crdtRef))
                {
                    crdtRef = Context.ActorOf(CRDTActor<TCrdt, TValue>.Props(service, identified.CrdtId));
                    crdts = crdts.SetItem(identified.CrdtId, crdtRef);
                }

                crdtRef.Forward(identified);
            });
        }
    }

    #region Messages

    internal interface ICrdtIdentified
    {
        string CrdtId { get; }
    }

    [Serializable]
    internal struct Get : ICrdtIdentified
    {
        public string CrdtId { get; private set; }

        public Get(string crdtId)
        {
            CrdtId = crdtId;
        }
    }

    [Serializable]
    internal struct GetReply<T> : ICrdtIdentified
    {
        public string CrdtId { get; private set; }
        public T Value { get; private set; }

        public GetReply(string crdtId, T value)
        {
            CrdtId = crdtId;
            Value = value;
        }
    }

    [Serializable]
    internal struct Update : ICrdtIdentified
    {
        public string CrdtId { get; private set; }
        public object Operation { get; private set; }

        public Update(string crdtId, object operation)
        {
            CrdtId = crdtId;
            Operation = operation;
        }
    }

    [Serializable]
    internal struct UpdateReply<T> : ICrdtIdentified
    {
        public string CrdtId { get; private set; }
        public T Value { get; private set; }

        public UpdateReply(string crdtId, T value)
        {
            CrdtId = crdtId;
            Value = value;
        }
    }

    [Serializable]
    internal struct Save : ICrdtIdentified
    {
        public string CrdtId { get; private set; }

        public Save(string crdtId)
        {
            CrdtId = crdtId;
        }
    }

    [Serializable]
    internal struct SaveReply : ICrdtIdentified
    {
        public string CrdtId { get; private set; }
        public SnapshotMetadata Metadata { get; private set; }

        public SaveReply(string crdtId, SnapshotMetadata metadata)
        {
            CrdtId = crdtId;
            Metadata = metadata;
        }
    }

    #endregion
}