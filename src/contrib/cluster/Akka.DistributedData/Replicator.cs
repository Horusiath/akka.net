﻿//-----------------------------------------------------------------------
// <copyright file="Replicator.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Actor;
using Akka.Cluster;
using Akka.DistributedData.Internal;
using Akka.Serialization;
using Akka.Util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using Akka.DistributedData.Durable;
using Akka.Event;
using Google.ProtocolBuffers;
using Status = Akka.DistributedData.Internal.Status;

namespace Akka.DistributedData
{
    using Digest = ByteString;

    /// <summary>
    /// <para>
    /// A replicated in-memory data store supporting low latency and high availability
    /// requirements.
    /// 
    /// The <see cref="Replicator"/> actor takes care of direct replication and gossip based
    /// dissemination of Conflict Free Replicated Data Types (CRDTs) to replicas in the
    /// the cluster.
    /// The data types must be convergent CRDTs and implement <see cref="IReplicatedData{T}"/>, i.e.
    /// they provide a monotonic merge function and the state changes always converge.
    /// 
    /// You can use your own custom <see cref="IReplicatedData{T}"/> or <see cref="IDeltaReplicatedData{T,TDelta}"/> types,
    /// and several types are provided by this package, such as:
    /// </para>
    /// <list type="bullet">
    ///     <item>
    ///         <term>Counters</term>
    ///         <description><see cref="GCounter"/>, <see cref="PNCounter"/></description> 
    ///     </item>
    ///     <item>
    ///         <term>Registers</term>
    ///         <description><see cref="LWWRegister{T}"/>, <see cref="Flag"/></description>
    ///     </item>
    ///     <item>
    ///         <term>Sets</term>
    ///         <description><see cref="GSet{T}"/>, <see cref="ORSet{T}"/></description>
    ///     </item>
    ///     <item>
    ///         <term>Maps</term> 
    ///         <description><see cref="ORDictionary{TKey,TValue}"/>, <see cref="ORMultiValueDictionary{TKey,TValue}"/>, <see cref="LWWDictionary{TKey,TValue}"/>, <see cref="PNCounterDictionary{TKey}"/></description>
    ///     </item>
    /// </list>
    /// <para>
    /// For good introduction to the CRDT subject watch the
    /// <a href="http://www.ustream.tv/recorded/61448875">The Final Causal Frontier</a>
    /// and <a href="http://vimeo.com/43903960">Eventually Consistent Data Structures</a>
    /// talk by Sean Cribbs and and the
    /// <a href="http://research.microsoft.com/apps/video/dl.aspx?id=153540">talk by Mark Shapiro</a>
    /// and read the excellent paper <a href="http://hal.upmc.fr/docs/00/55/55/88/PDF/techreport.pdf">
    /// A comprehensive study of Convergent and Commutative Replicated Data Types</a>
    /// by Mark Shapiro et. al.
    /// </para>
    /// <para>
    /// The <see cref="Replicator"/> actor must be started on each node in the cluster, or group of
    /// nodes tagged with a specific role. It communicates with other <see cref="Replicator"/> instances
    /// with the same path (without address) that are running on other nodes . For convenience it
    /// can be used with the <see cref="DistributedData"/> extension but it can also be started as an ordinary
    /// actor using the <see cref="Props"/>. If it is started as an ordinary actor it is important
    /// that it is given the same name, started on same path, on all nodes.
    /// </para>
    /// <para>
    /// <a href="paper http://arxiv.org/abs/1603.01529">Delta State Replicated Data Types</a>
    /// is supported. delta-CRDT is a way to reduce the need for sending the full state
    /// for updates. For example adding element 'c' and 'd' to set {'a', 'b'} would
    /// result in sending the delta {'c', 'd'} and merge that with the state on the
    /// receiving side, resulting in set {'a', 'b', 'c', 'd'}.
    /// </para>
    /// <para>
    /// The protocol for replicating the deltas supports causal consistency if the data type
    /// is marked with <see cref="IRequireCausualDeliveryOfDeltas"/>. Otherwise it is only eventually
    /// consistent. Without causal consistency it means that if elements 'c' and 'd' are
    /// added in two separate <see cref="Update"/> operations these deltas may occasionally be propagated
    /// to nodes in different order than the causal order of the updates. For this example it
    /// can result in that set {'a', 'b', 'd'} can be seen before element 'c' is seen. Eventually
    /// it will be {'a', 'b', 'c', 'd'}.
    /// </para>
    /// <para>
    /// == Update ==
    /// 
    /// To modify and replicate a <see cref="IReplicatedData{T}"/> value you send a <see cref="Update"/> message
    /// to the local <see cref="Replicator"/>.
    /// The current data value for the `key` of the <see cref="Update"/> is passed as parameter to the `modify`
    /// function of the <see cref="Update"/>. The function is supposed to return the new value of the data, which
    /// will then be replicated according to the given consistency level.
    /// 
    /// The `modify` function is called by the `Replicator` actor and must therefore be a pure
    /// function that only uses the data parameter and stable fields from enclosing scope. It must
    /// for example not access `sender()` reference of an enclosing actor.
    /// 
    /// <see cref="Update"/> is intended to only be sent from an actor running in same local `ActorSystem` as
    /// the <see cref="Replicator"/>, because the `modify` function is typically not serializable.
    /// 
    /// You supply a write consistency level which has the following meaning:
    /// <list type="bullet">
    ///     <item>
    ///         <term><see cref="WriteLocal"/></term>
    ///         <description>
    ///             The value will immediately only be written to the local replica, and later disseminated with gossip.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="WriteTo"/></term>
    ///         <description>
    ///             The value will immediately be written to at least <see cref="WriteTo.N"/> replicas, including the local replica.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="WriteMajority"/></term>
    ///         <description>
    ///             The value will immediately be written to a majority of replicas, i.e. at least `N/2 + 1` replicas, 
    ///             where N is the number of nodes in the cluster (or cluster role group).
    ///         </description>     
    ///     </item>
    ///     <item>
    ///         <term><see cref="WriteAll"/></term> 
    ///         <description>
    ///             The value will immediately be written to all nodes in the cluster (or all nodes in the cluster role group).
    ///         </description>
    ///     </item>
    /// </list>
    /// 
    /// As reply of the <see cref="Update"/> a <see cref="UpdateSuccess"/> is sent to the sender of the
    /// <see cref="Update"/> if the value was successfully replicated according to the supplied consistency
    /// level within the supplied timeout. Otherwise a <see cref="IUpdateFailure"/> subclass is
    /// sent back. Note that a <see cref="UpdateTimeout"/> reply does not mean that the update completely failed
    /// or was rolled back. It may still have been replicated to some nodes, and will eventually
    /// be replicated to all nodes with the gossip protocol.
    /// 
    /// You will always see your own writes. For example if you send two <see cref="Update"/> messages
    /// changing the value of the same `key`, the `modify` function of the second message will
    /// see the change that was performed by the first <see cref="Update"/> message.
    /// 
    /// In the <see cref="Update"/> message you can pass an optional request context, which the <see cref="Replicator"/>
    /// does not care about, but is included in the reply messages. This is a convenient
    /// way to pass contextual information (e.g. original sender) without having to use <see cref="Ask"/>
    /// or local correlation data structures.
    /// </para>
    /// <para>
    /// == Get ==
    /// 
    /// To retrieve the current value of a data you send <see cref="Get"/> message to the
    /// <see cref="Replicator"/>. You supply a consistency level which has the following meaning:
    /// <list type="bullet">
    ///     <item>
    ///         <term><see cref="ReadLocal"/></term> 
    ///         <description>The value will only be read from the local replica.</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="ReadFrom"/></term> 
    ///         <description>The value will be read and merged from <see cref="ReadFrom.N"/> replicas, including the local replica.</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="ReadMajority"/></term>
    ///         <description>
    ///             The value will be read and merged from a majority of replicas, i.e. at least `N/2 + 1` replicas, where N is the number of nodes in the cluster (or cluster role group).
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="ReadAll"/></term>
    ///         <description>The value will be read and merged from all nodes in the cluster (or all nodes in the cluster role group).</description>
    ///     </item>
    /// </list>
    /// 
    /// As reply of the <see cref="Get"/> a <see cref="GetSuccess"/> is sent to the sender of the
    /// <see cref="Get"/> if the value was successfully retrieved according to the supplied consistency
    /// level within the supplied timeout. Otherwise a <see cref="GetFailure"/> is sent.
    /// If the key does not exist the reply will be <see cref="NotFound"/>.
    /// 
    /// You will always read your own writes. For example if you send a <see cref="Update"/> message
    /// followed by a <see cref="Get"/> of the same `key` the <see cref="Get"/> will retrieve the change that was
    /// performed by the preceding <see cref="Update"/> message. However, the order of the reply messages are
    /// not defined, i.e. in the previous example you may receive the <see cref="GetSuccess"/> before
    /// the <see cref="UpdateSuccess"/>.
    /// 
    /// In the <see cref="Get"/> message you can pass an optional request context in the same way as for the
    /// <see cref="Update"/> message, described above. For example the original sender can be passed and replied
    /// to after receiving and transforming <see cref="GetSuccess"/>.
    /// </para>
    /// <para>
    /// == Subscribe ==
    /// 
    /// You may also register interest in change notifications by sending <see cref="Subscribe"/>
    /// message to the <see cref="Replicator"/>. It will send <see cref="Changed"/> messages to the registered
    /// subscriber when the data for the subscribed key is updated. Subscribers will be notified
    /// periodically with the configured `notify-subscribers-interval`, and it is also possible to
    /// send an explicit <see cref="FlushChanges"/> message to the <see cref="Replicator"/> to notify the subscribers
    /// immediately.
    /// 
    /// The subscriber is automatically removed if the subscriber is terminated. A subscriber can
    /// also be deregistered with the <see cref="Unsubscribe"/> message.
    /// </para>
    /// <para>
    /// == Delete ==
    /// 
    /// A data entry can be deleted by sending a <see cref="Delete"/> message to the local
    /// local <see cref="Replicator"/>. As reply of the <see cref="Delete"/> a <see cref="DeleteSuccess"/> is sent to
    /// the sender of the <see cref="Delete"/> if the value was successfully deleted according to the supplied
    /// consistency level within the supplied timeout. Otherwise a <see cref="ReplicationDeleteFailure"/>
    /// is sent. Note that <see cref="ReplicationDeleteFailure"/> does not mean that the delete completely failed or
    /// was rolled back. It may still have been replicated to some nodes, and may eventually be replicated
    /// to all nodes.
    /// 
    /// A deleted key cannot be reused again, but it is still recommended to delete unused
    /// data entries because that reduces the replication overhead when new nodes join the cluster.
    /// Subsequent <see cref="Delete"/>, <see cref="Update"/> and <see cref="Get"/> requests will be replied with <see cref="DataDeleted"/>.
    /// Subscribers will receive <see cref="Deleted"/>.
    /// 
    /// In the <see cref="Delete"/> message you can pass an optional request context in the same way as for the
    /// <see cref="Update"/> message, described above. For example the original sender can be passed and replied
    /// to after receiving and transforming <see cref="DeleteSuccess"/>.
    /// </para>
    /// <para>
    /// == CRDT Garbage ==
    /// 
    /// One thing that can be problematic with CRDTs is that some data types accumulate history (garbage).
    /// For example a <see cref="GCounter"/> keeps track of one counter per node. If a <see cref="GCounter"/> has been updated
    /// from one node it will associate the identifier of that node forever. That can become a problem
    /// for long running systems with many cluster nodes being added and removed. To solve this problem
    /// the <see cref="Replicator"/> performs pruning of data associated with nodes that have been removed from the
    /// cluster. Data types that need pruning have to implement <see cref="IRemovedNodePruning{T}"/>. The pruning consists
    /// of several steps:
    /// <list type="">
    ///     <item>When a node is removed from the cluster it is first important that all updates that were
    /// done by that node are disseminated to all other nodes. The pruning will not start before the
    /// <see cref="MaxPruningDissemination"/> duration has elapsed. The time measurement is stopped when any
    /// replica is unreachable, but it's still recommended to configure this with certain margin.
    /// It should be in the magnitude of minutes.</item>
    /// <item>The nodes are ordered by their address and the node ordered first is called leader.
    /// The leader initiates the pruning by adding a <see cref="PruningInitialized"/> marker in the data envelope.
    /// This is gossiped to all other nodes and they mark it as seen when they receive it.</item>
    /// <item>When the leader sees that all other nodes have seen the <see cref="PruningInitialized"/> marker
    /// the leader performs the pruning and changes the marker to <see cref="PruningPerformed"/> so that nobody
    /// else will redo the pruning. The data envelope with this pruning state is a CRDT itself.
    /// The pruning is typically performed by "moving" the part of the data associated with
    /// the removed node to the leader node. For example, a <see cref="GCounter"/> is a `Map` with the node as key
    /// and the counts done by that node as value. When pruning the value of the removed node is
    /// moved to the entry owned by the leader node. See [[RemovedNodePruning#prune]].</item>
    /// <item>Thereafter the data is always cleared from parts associated with the removed node so that
    /// it does not come back when merging. See [[RemovedNodePruning#pruningCleanup]]</item>
    /// <item>After another `maxPruningDissemination` duration after pruning the last entry from the
    /// removed node the <see cref="PruningPerformed"/> markers in the data envelope are collapsed into a
    /// single tombstone entry, for efficiency. Clients may continue to use old data and therefore
    /// all data are always cleared from parts associated with tombstoned nodes. </item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class Replicator : ReceiveActor
    {
        public static Props Props(ReplicatorSettings settings) => 
            Actor.Props.Create(() => new Replicator(settings)).WithDeploy(Deploy.Local).WithDispatcher(settings.Dispatcher);

        private static readonly Digest DeletedDigest = ByteString.Empty;
        private static readonly Digest LazyDigest = ByteString.Unsafe.FromBytes(new byte[] { 0 });
        private static readonly Digest NotFoundDigest = ByteString.Unsafe.FromBytes(new byte[] { 255 });

        private static readonly DataEnvelope DeletedEnvelope = new DataEnvelope(DeletedData.Instance);

        private readonly ReplicatorSettings _settings;

        private readonly Cluster.Cluster _cluster;
        private readonly Address _selfAddress;
        private readonly UniqueAddress _selfUniqueAddress;

        private readonly ICancelable _gossipTask;
        private readonly ICancelable _notifyTask;
        private readonly ICancelable _pruningTask;
        private readonly ICancelable _clockTask;

        private readonly Serializer _serializer;
        private readonly long _maxPruningDisseminationNanos;

        private ImmutableHashSet<Address> _nodes = ImmutableHashSet<Address>.Empty;

        private ImmutableDictionary<UniqueAddress, long> _removedNodes = ImmutableDictionary<UniqueAddress, long>.Empty;
        private ImmutableDictionary<UniqueAddress, long> _pruningPerformed = ImmutableDictionary<UniqueAddress, long>.Empty;
        private ImmutableHashSet<UniqueAddress> _tombstonedNodes = ImmutableHashSet<UniqueAddress>.Empty;

        private Address _leader = null;
        private bool IsLeader => _leader != null && _leader == _selfAddress;

        private long _previousClockTime;
        private long _allReachableClockTime = 0;
        private ImmutableHashSet<Address> _unreachable = ImmutableHashSet<Address>.Empty;

        private ImmutableDictionary<string, Tuple<DataEnvelope, ByteString>> _dataEntries = ImmutableDictionary<string, Tuple<DataEnvelope, ByteString>>.Empty;
        private ImmutableHashSet<string> _changed = ImmutableHashSet<string>.Empty;

        private long _statusCount = 0;
        private int _statusTotChunks = 0;

        private readonly Dictionary<string, HashSet<IActorRef>> _subscribers = new Dictionary<string, HashSet<IActorRef>>();
        private readonly Dictionary<string, HashSet<IActorRef>> _newSubscribers = new Dictionary<string, HashSet<IActorRef>>();
        private ImmutableDictionary<string, IKey> _subscriptionKeys = ImmutableDictionary<string, IKey>.Empty;

        private readonly ILoggingAdapter _log;

        private readonly bool _hasDurableKeys;
        private readonly IImmutableSet<string> _durableKeys;
        private readonly IImmutableSet<string> _durableWildcards;
        private readonly IActorRef _durableStore;

        public Replicator(ReplicatorSettings settings)
        {
            _settings = settings;
            _cluster = Cluster.Cluster.Get(Context.System);
            _selfAddress = _cluster.SelfAddress;
            _selfUniqueAddress = _cluster.SelfUniqueAddress;
            _log = Context.GetLogger();

            if (_cluster.IsTerminated) throw new ArgumentException("Cluster node must not be terminated");
            if (!string.IsNullOrEmpty(_settings.Role) && !_cluster.SelfRoles.Contains(_settings.Role))
                throw new ArgumentException($"The cluster node {_selfAddress} does not have the role {_settings.Role}");

            _gossipTask = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(_settings.GossipInterval, _settings.GossipInterval, Self, GossipTick.Instance, Self);
            _notifyTask = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(_settings.NotifySubscribersInterval, _settings.NotifySubscribersInterval, Self, FlushChanges.Instance, Self);
            _pruningTask = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(_settings.PruningInterval, _settings.PruningInterval, Self, RemovedNodePruningTick.Instance, Self);
            _clockTask = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(_settings.GossipInterval, _settings.GossipInterval, Self, ClockTick.Instance, Self);

            _serializer = Context.System.Serialization.FindSerializerForType(typeof(DataEnvelope));
            _maxPruningDisseminationNanos = _settings.MaxPruningDissemination.Ticks * 100;

            _previousClockTime = DateTime.UtcNow.Ticks * 100;

            _hasDurableKeys = settings.DurableKeys.Count > 0;
            var durableKeysBuilder = ImmutableHashSet<string>.Empty.ToBuilder();
            var durableWildcardsBuilder = ImmutableHashSet<string>.Empty.ToBuilder();
            foreach (var key in settings.DurableKeys)
            {
                if (key.EndsWith("*"))
                    durableWildcardsBuilder.Add(key.Substring(0, key.Length - 1));
                else
                    durableKeysBuilder.Add(key);
            }

            _durableKeys = durableKeysBuilder.ToImmutable();
            _durableWildcards = durableWildcardsBuilder.ToImmutable();

            _durableStore = _hasDurableKeys
                ? Context.Watch(Context.ActorOf(_settings.DurableStoreProps, "durableStore"))
                : Context.System.DeadLetters;

           if (_hasDurableKeys) Load();
           else NormalReceive();
        }

        protected override void PreStart()
        {
            if (_hasDurableKeys) _durableStore.Tell(LoadAll.Instance);

            var leaderChangedClass = _settings.Role != null
                ? typeof(ClusterEvent.RoleLeaderChanged)
                : typeof(ClusterEvent.LeaderChanged);
            _cluster.Subscribe(Self, ClusterEvent.SubscriptionInitialStateMode.InitialStateAsEvents,
                typeof(ClusterEvent.IMemberEvent), typeof(ClusterEvent.IReachabilityEvent), leaderChangedClass);
        }

        protected override void PostStop()
        {
            _cluster.Unsubscribe(Self);
            _gossipTask.Cancel();
            _notifyTask.Cancel();
            _pruningTask.Cancel();
            _clockTask.Cancel();
        }

        protected override SupervisorStrategy SupervisorStrategy() => new OneForOneStrategy(e =>
        {
            var fromDurableStore = Equals(Sender, _durableStore) && !Equals(Sender, Context.System.DeadLetters);
            if ((e is LoadFailedException || e is ActorInitializationException) && fromDurableStore)
            {
                _log.Error(e,
                    "Stopping distributed-data Replicator due to load or startup failure in durable store, caused by: {0}",
                    e.Message);
                Context.Stop(Self);
                return Directive.Stop;
            }
            else return Actor.SupervisorStrategy.DefaultDecider.Decide(e);
        });
        
        private void NormalReceive()
        {
            Receive<Get>(g => ReceiveGet(g));
            Receive<Update>(msg => ReceiveUpdate(msg.Key, msg.Modify, msg.Consistency, msg.Request));
            Receive<Read>(r => ReceiveRead(r.Key));
            Receive<Write>(w => ReceiveWrite(w.Key, w.Envelope));
            Receive<ReadRepair>(rr => ReceiveReadRepair(rr.Key, rr.Envelope));
            Receive<FlushChanges>(_ => ReceiveFlushChanges());
            Receive<GossipTick>(_ => ReceiveGossipTick());
            Receive<ClockTick>(c => ReceiveClockTick());
            Receive<Internal.Status>(s => ReceiveStatus(s.Digests, s.Chunk, s.TotalChunks));
            Receive<Gossip>(g => ReceiveGossip(g.UpdatedData, g.SendBack));
            Receive<Subscribe>(s => ReceiveSubscribe(s.Key, s.Subscriber));
            Receive<Unsubscribe>(u => ReceiveUnsubscribe(u.Key, u.Subscriber));
            Receive<Terminated>(t => ReceiveTerminated(t.ActorRef));
            Receive<ClusterEvent.MemberUp>(m => ReceiveMemberUp(m.Member));
            Receive<ClusterEvent.MemberRemoved>(m => ReceiveMemberRemoved(m.Member));
            Receive<ClusterEvent.IMemberEvent>(_ => { });
            Receive<ClusterEvent.UnreachableMember>(u => ReceiveUnreachable(u.Member));
            Receive<ClusterEvent.ReachableMember>(r => ReceiveReachable(r.Member));
            Receive<ClusterEvent.LeaderChanged>(l => ReceiveLeaderChanged(l.Leader, null));
            Receive<ClusterEvent.RoleLeaderChanged>(r => ReceiveLeaderChanged(r.Leader, r.Role));
            Receive<GetKeyIds>(_ => ReceiveGetKeyIds());
            Receive<Delete>(d => ReceiveDelete(d.Key, d.Consistency, d.Request));
            Receive<RemovedNodePruningTick>(r => ReceiveRemovedNodePruningTick());
            Receive<GetReplicaCount>(_ => ReceiveGetReplicaCount());
        }

        private void Load()
        {
            var startTime = DateTime.UtcNow;
            var count = 0;

            NormalReceive();

            Receive<LoadData>(load =>
            {
                count += load.Data.Count;
                foreach (var entry in load.Data)
                {
                    var envelope = entry.Value.Data;
                    var newEnvelope = Write(entry.Key, envelope);
                    if (!ReferenceEquals(newEnvelope.Data, envelope.Data))
                    {
                        _durableStore.Tell(new Store(entry.Key, new DurableDataEnvelope(newEnvelope), null));
                    }
                }
            });
            Receive<LoadAllCompleted>(_ =>
            {
                _log.Debug("Loading {0} entries from durable store took {1} ms", count,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);
                Become(NormalReceive);
                Self.Tell(FlushChanges.Instance);
            });
            Receive<GetReplicaCount>(_ =>
            {
                Sender.Tell(new ReplicaCount(0));
            });

            // ignore scheduled ticks when loading durable data
            Receive(new Action<RemovedNodePruningTick>(Ignore));
            Receive(new Action<FlushChanges>(Ignore));
            Receive(new Action<GossipTick>(Ignore));

            // ignore gossip and replication when loading durable data
            Receive(new Action<Read>(IgnoreDebug));
            Receive(new Action<Write>(IgnoreDebug));
            Receive(new Action<Status>(IgnoreDebug));
            Receive(new Action<Gossip>(IgnoreDebug));
        }

        private void IgnoreDebug<T>(T msg)
        {
            _log.Debug("ignoring message [{0}] when loading durable data", typeof(T));
        }

        private void Ignore<T>(T msg) { }


        private void ReceiveGet(Get message)
        {
            var key = message.Key;
            var consistency = message.Consistency;
            var req = message.Request;
            var localValue = GetData(key.Id);

            _log.Debug("Received get for key {0}, local value {1}, consistency: {2}", key.Id, localValue, consistency);

            if (IsLocalGet(consistency))
            {
                if (localValue == null) Sender.Tell(new NotFound(key, req));
                else if (localValue.Data is DeletedData) Sender.Tell(new DataDeleted(key, message.Request));
                else Sender.Tell(new GetSuccess(key, req, localValue.Data));
            }
            else
                Context.ActorOf(ReadAggregator.Props(key, consistency, req, _nodes, _unreachable, localValue, Sender)
                        .WithDispatcher(Context.Props.Dispatcher));
        }

        private bool IsLocalGet(IReadConsistency consistency)
        {
            if (consistency is ReadLocal) return true;
            if (consistency is ReadAll || consistency is ReadMajority) return _nodes.Count == 0;
            return false;
        }

        private void ReceiveRead(string key)
        {
            _log.Debug("Received read '{0}' from {1}", key, Sender);
            Sender.Tell(new ReadResult(GetData(key)));
        } 

        private bool MatchingRole(Member m) => string.IsNullOrEmpty(_settings.Role) || m.HasRole(_settings.Role);
        
        private bool IsDurable(string key) => _durableKeys.Contains(key) || (_durableWildcards.Count > 0 && _durableWildcards.Any(key.StartsWith));

        private void ReceiveUpdate(IKey key, Func<IReplicatedData, IReplicatedData> modify, IWriteConsistency consistency, object request)
        {
            try
            {
                var localValue = GetData(key.Id);
                IReplicatedData modifiedValue;
                if (localValue == null) modifiedValue = modify(null);
                else if (localValue.Data is DeletedData)
                {
                    _log.Debug("Received update for deleted key {0}", key);
                    Sender.Tell(new DataDeleted(key, request));
                    return;
                }
                else
                {
                    var existing = localValue.Data;
                    modifiedValue = existing.Merge(modify(existing));
                }

                _log.Debug("Received Update for key {0}, old data {1}, new data {2}", key, localValue, modifiedValue);
                var envelope = new DataEnvelope(PruningCleanupTombstoned(modifiedValue));
                SetData(key.Id, envelope);
                var durable = IsDurable(key.Id);
                if (IsLocalUpdate(consistency))
                {
                    if (durable)
                    {
                        var reply = new StoreReply(
                            successMessage: new UpdateSuccess(key, request),
                            failureMessage: new StoreFailure(key, request),
                            replyTo: Sender);
                        _durableStore.Tell(new Store(key.Id, new DurableDataEnvelope(envelope), reply));
                    }
                    else Sender.Tell(new UpdateSuccess(key, request));
                }
                else
                {
                    var writeAggregator = Context.ActorOf(WriteAggregator.Props(key, envelope, consistency, request, _nodes, _unreachable, Sender, durable).WithDispatcher(Context.Props.Dispatcher));

                    if (durable)
                    {
                        var reply = new StoreReply(
                            successMessage: new UpdateSuccess(key, request),
                            failureMessage: new StoreFailure(key, request),
                            replyTo: writeAggregator);
                        _durableStore.Tell(new Store(key.Id, new DurableDataEnvelope(envelope), reply));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Received update for key {0}, failed {1}", key, ex.Message);
                Sender.Tell(new ModifyFailure(key, "Update failed: " + ex.Message, ex, request));
            }
        }

        private bool IsLocalUpdate(IWriteConsistency consistency)
        {
            if (consistency is WriteLocal)
            {
                return true;
            }
            if (consistency is WriteAll || consistency is WriteMajority)
            {
                return _nodes.Count == 0;
            }
            return false;
        }

        private void ReceiveWrite(string key, DataEnvelope envelope)
        {
            var newEnvelope = Write(key, envelope);
            if (newEnvelope != null)
            {
                if (IsDurable(key))
                    _durableStore.Tell(new Store(key, new DurableDataEnvelope(newEnvelope),
                        new StoreReply(WriteAck.Instance, WriteNack.Instance, Sender)));
                else Sender.Tell(WriteAck.Instance);
            }
        }

        private DataEnvelope Write(string key, DataEnvelope writeEnvelope)
        {
            var envelope = GetData(key);
            if (envelope != null)
            {
                if (envelope.Data is DeletedData) return writeEnvelope; // already deleted
                else
                {
                    var existing = envelope.Data;
                    if (existing.GetType() == writeEnvelope.Data.GetType() || writeEnvelope.Data is DeletedData)
                    {
                        var merged = envelope.Merge(PruningCleanupTombstoned(writeEnvelope).AddSeen(_selfAddress));
                        SetData(key, merged);
                        return merged;
                    }
                    else
                    {
                        _log.Warning("Wrong type for writing {0}, existing type {1}, got {2}", key,
                            existing.GetType().Name, writeEnvelope.Data.GetType().Name);
                        return null;
                    }
                }
            }
            else
            {
                var cleaned = PruningCleanupTombstoned(writeEnvelope).AddSeen(_selfAddress);
                SetData(key, cleaned);
                return cleaned;
            }
        }

        private void ReceiveReadRepair(string key, DataEnvelope writeEnvelope)
        {
            WriteAndStore(key, writeEnvelope);
            Sender.Tell(ReadRepairAck.Instance);
        }

        private void WriteAndStore(string key, DataEnvelope writeEnvelope)
        {
            var newEnvelope = Write(key, writeEnvelope);
            if (newEnvelope != null && IsDurable(key))
            {
                _durableStore.Tell(new Store(key, new DurableDataEnvelope(newEnvelope), null));
            }
        }

        private void ReceiveGetKeyIds()
        {
            var keys = _dataEntries.Where(kvp => !(kvp.Value.Item1.Data is DeletedData))
                                   .Select(x => x.Key)
                                   .ToImmutableHashSet();
            Sender.Tell(new GetKeysIdsResult(keys));
        }

        private void ReceiveDelete(IKey key, IWriteConsistency consistency, object request)
        {
            var envelope = GetData(key.Id);
            if (envelope != null)
            {
                if (envelope.Data is DeletedData) Sender.Tell(new DataDeleted(key, request));
                else
                {
                    SetData(key.Id, DeletedEnvelope);
                    var durable = IsDurable(key.Id);
                    if (IsLocalUpdate(consistency))
                    {
                        if (durable)
                        {
                            var reply = new StoreReply(
                                successMessage: new DeleteSuccess(key, request),
                                failureMessage: new StoreFailure(key, request),
                                replyTo: Sender);
                            _durableStore.Tell(new Store(key.Id, new DurableDataEnvelope(DeletedEnvelope), reply));
                        }
                        Sender.Tell(new DeleteSuccess(key, request));
                    }
                    else
                    {
                        var writeAggregator = Context.ActorOf(WriteAggregator.Props(key, DeletedEnvelope, consistency, null, _nodes, _unreachable, Sender, durable)
                            .WithDispatcher(Context.Props.Dispatcher));

                        if (durable)
                        {
                            var reply = new StoreReply(
                                successMessage: new DeleteSuccess(key, request),
                                failureMessage: new StoreFailure(key, request),
                                replyTo: writeAggregator);
                            _durableStore.Tell(new Store(key.Id, new DurableDataEnvelope(DeletedEnvelope), reply));
                        }
                    }
                }
            }
        }

        private void SetData(string key, DataEnvelope envelope)
        {
            ByteString digest;
            if (_subscribers.ContainsKey(key) && !_changed.Contains(key))
            {
                var oldDigest = GetDigest(key);
                var dig = Digest(envelope);
                if (!Equals(dig, oldDigest))
                    _changed = _changed.Add(key);

                digest = dig;
            }
            else if (envelope.Data is DeletedData) digest = DeletedDigest;
            else digest = LazyDigest;

            _dataEntries = _dataEntries.SetItem(key, Tuple.Create(envelope, digest));
        }

        private ByteString GetDigest(string key)
        {
            Tuple<DataEnvelope, ByteString> value;
            var contained = _dataEntries.TryGetValue(key, out value);
            if (contained)
            {
                if (Equals(value.Item2, LazyDigest))
                {
                    var digest = Digest(value.Item1);
                    _dataEntries = _dataEntries.SetItem(key, Tuple.Create(value.Item1, digest));
                    return digest;
                }
                else
                {
                    return value.Item2;
                }
            }
            else
            {
                return NotFoundDigest;
            }
        }

        private ByteString Digest(DataEnvelope envelope)
        {
            if (Equals(envelope.Data, DeletedData.Instance))
            {
                return DeletedDigest;
            }
            else
            {
                var bytes = _serializer.ToBinary(envelope);
                var serialized = MD5.Create().ComputeHash(bytes);
                return ByteString.CopyFrom(serialized);
            }
        }

        private DataEnvelope GetData(string key)
        {
            Tuple<DataEnvelope, ByteString> value;
            if (!_dataEntries.TryGetValue(key, out value))
            {
                return null;
            }
            return value.Item1;
        }

        private void Notify(string keyId, ISet<IActorRef> subs)
        {
            var key = _subscriptionKeys[keyId];
            var data = GetData(keyId);
            if (data != null)
            {
                var msg = data.Data is DeletedData
                    ? (object)new DataDeleted(key, null)
                    : new Changed(key, data.Data);

                foreach (var sub in subs) sub.Tell(msg);
            }
        }

        private void ReceiveFlushChanges()
        {
            if (_subscribers.Count != 0)
            {
                foreach (var key in _changed)
                {
                    HashSet<IActorRef> subs;
                    if (_subscribers.TryGetValue(key, out subs))
                        Notify(key, subs);
                }
            }

            // Changed event is sent to new subscribers even though the key has not changed,
            // i.e. send current value
            if (_newSubscribers.Count != 0)
            {
                foreach (var kvp in _newSubscribers)
                {
                    Notify(kvp.Key, kvp.Value);
                    HashSet<IActorRef> set;
                    if (!_subscribers.TryGetValue(kvp.Key, out set))
                    {
                        set = new HashSet<IActorRef>();
                        _subscribers.Add(kvp.Key, set);
                    }

                    foreach (var sub in kvp.Value) set.Add(sub);
                }

                _newSubscribers.Clear();
            }

            _changed = ImmutableHashSet<string>.Empty;
        }

        private void ReceiveGossipTick()
        {
            var node = SelectRandomNode(_nodes);
            if (node != null)
            {
                GossipTo(node);
            }
        }

        private void GossipTo(Address address)
        {
            var to = Replica(address);
            if (_dataEntries.Count <= _settings.MaxDeltaElements)
            {
                var status = new Internal.Status(_dataEntries.Select(x => new KeyValuePair<string, ByteString>(x.Key, GetDigest(x.Key))).ToImmutableDictionary(), 0, 1);
                to.Tell(status);
            }
            else
            {
                var totChunks = _dataEntries.Count / _settings.MaxDeltaElements;
                for (var i = 1; i <= Math.Min(totChunks, 10); i++)
                {
                    if (totChunks == _statusTotChunks)
                    {
                        _statusCount++;
                    }
                    else
                    {
                        _statusCount = ThreadLocalRandom.Current.Next(0, totChunks);
                        _statusTotChunks = totChunks;
                    }
                    var chunk = (int)(_statusCount % totChunks);
                    var entries = _dataEntries.Where(x => Math.Abs(x.Key.GetHashCode()) % totChunks == chunk)
                                              .Select(x => new KeyValuePair<string, ByteString>(x.Key, GetDigest(x.Key)))
                                              .ToImmutableDictionary();
                    var status = new Internal.Status(entries, chunk, totChunks);
                    to.Tell(status);
                }
            }
        }

        private Address SelectRandomNode(ImmutableHashSet<Address> addresses)
        {
            if (addresses.Count > 0)
            {
                var random = ThreadLocalRandom.Current.Next(addresses.Count - 1);
                return addresses.Skip(random).First();
            }

            return null;
        }

        private ActorSelection Replica(Address address) => Context.ActorSelection(Self.Path.ToStringWithAddress(address));

        private bool IsOtherDifferent(string key, ByteString otherDigest)
        {
            var d = GetDigest(key);
            var isFound = !Equals(d, NotFoundDigest);
            var isEqualToOther = Equals(d, otherDigest);
            return isFound && !isEqualToOther;
        }

        private void ReceiveStatus(IImmutableDictionary<string, ByteString> otherDigests, int chunk, int totChunks)
        {
            if (_log.IsDebugEnabled)
                _log.Debug("Received gossip status from [{0}], chunk {1}/{2} containing [{3}]", Sender.Path.Address, chunk + 1, totChunks, string.Join(", ", otherDigests.Keys));

            // if no data was send we do nothing
            if (otherDigests.Count == 0)
                return;

            var otherDifferentKeys = otherDigests
                .Where(x => IsOtherDifferent(x.Key, x.Value))
                .Select(x => x.Key)
                .ToImmutableHashSet();

            var otherKeys = otherDigests.Keys.ToImmutableHashSet();
            var myKeys = (totChunks == 1
                ? _dataEntries.Keys
                : _dataEntries.Keys.Where(x => Math.Abs(x.GetHashCode()) % totChunks == chunk))
                .ToImmutableHashSet();
            
            var otherMissingKeys = myKeys.Except(otherKeys);

            var keys = otherDifferentKeys
                .Union(otherMissingKeys)
                .Take(_settings.MaxDeltaElements)
                .ToArray();

            if (keys.Length != 0)
            {
                if (_log.IsDebugEnabled)
                    _log.Debug("Sending gossip to [{0}]: {1}", Sender.Path.Address, string.Join(", ", keys));

                var g = new Gossip(keys.Select(k => new KeyValuePair<string, DataEnvelope>(k, GetData(k))).ToImmutableDictionary(), !otherDifferentKeys.IsEmpty);
                Sender.Tell(g);
            }

            var myMissingKeys = otherKeys.Except(myKeys);
            if (!myMissingKeys.IsEmpty)
            {
                if (Context.System.Log.IsDebugEnabled)
                    Context.System.Log.Debug("Sending gossip status to {0}, requesting missing {1}", Sender.Path.Address, string.Join(", ", myMissingKeys));

                var status = new Internal.Status(myMissingKeys.Select(x => new KeyValuePair<string, ByteString>(x, NotFoundDigest)).ToImmutableDictionary(), chunk, totChunks);
                Sender.Tell(status);
            }
        }

        private void ReceiveGossip(IImmutableDictionary<string, DataEnvelope> updatedData, bool sendBack)
        {
            if (Context.System.Log.IsDebugEnabled)
            {
                Context.System.Log.Debug("Received gossip from [{0}], containing [{1}]", Sender.Path.Address, String.Join(", ", updatedData.Keys));
            }
            var replyData = ImmutableDictionary<String, DataEnvelope>.Empty.ToBuilder();
            foreach (var d in updatedData)
            {
                var key = d.Key;
                var envelope = d.Value;
                var hadData = _dataEntries.ContainsKey(key);
                WriteAndStore(key, envelope);
                if (sendBack)
                {
                    var data = GetData(key);
                    if (data != null)
                    {
                        if (hadData || data.Pruning.Count != 0)
                        {
                            replyData.Add(key, data);
                        }
                    }
                }
            }
            if (sendBack && replyData.Count != 0)
            {
                Sender.Tell(new Gossip(replyData.ToImmutable(), false));
            }
        }

        private void ReceiveSubscribe(IKey key, IActorRef subscriber)
        {
            HashSet<IActorRef> set;
            if (!_newSubscribers.TryGetValue(key.Id, out set))
            {
                _newSubscribers[key.Id] = set = new HashSet<IActorRef>();
            }
            set.Add(subscriber);

            if (!_subscriptionKeys.ContainsKey(key.Id))
                _subscriptionKeys = _subscriptionKeys.SetItem(key.Id, key);

            Context.Watch(subscriber);
        }

        private void ReceiveUnsubscribe(IKey key, IActorRef subscriber)
        {
            HashSet<IActorRef> set;
            if (_subscribers.TryGetValue(key.Id, out set) && set.Remove(subscriber) && set.Count == 0)
                _subscribers.Remove(key.Id);

            if (_newSubscribers.TryGetValue(key.Id, out set) && set.Remove(subscriber) && set.Count == 0)
                _newSubscribers.Remove(key.Id);

            if (!HasSubscriber(subscriber))
                Context.Unwatch(subscriber);

            if (!_subscribers.ContainsKey(key.Id) || !_newSubscribers.ContainsKey(key.Id))
                _subscriptionKeys = _subscriptionKeys.Remove(key.Id);
        }

        private bool HasSubscriber(IActorRef subscriber)
        {
            return _subscribers.Any(kvp => kvp.Value.Contains(subscriber)) ||
                _newSubscribers.Any(kvp => kvp.Value.Contains(subscriber));
        }

        private void ReceiveTerminated(IActorRef terminated)
        {
            if (Equals(terminated, _durableStore))
            {
                _log.Error("Stopping distributed-data replicator because durable store terminated");
                Context.Stop(Self);
            }
            else
            {
                var keys1 = _subscribers.Where(x => x.Value.Contains(terminated))
                                        .Select(x => x.Key)
                                        .ToImmutableHashSet();

                foreach (var k in keys1)
                {
                    HashSet<IActorRef> set;
                    if (_subscribers.TryGetValue(k, out set) && set.Remove(terminated) && set.Count == 0)
                        _subscribers.Remove(k);
                }

                var keys2 = _newSubscribers.Where(x => x.Value.Contains(terminated))
                                           .Select(x => x.Key)
                                           .ToImmutableHashSet();

                foreach (var k in keys2)
                {
                    HashSet<IActorRef> set;
                    if (_newSubscribers.TryGetValue(k, out set) && set.Remove(terminated) && set.Count == 0)
                        _newSubscribers.Remove(k);
                }

                var allKeys = keys1.Union(keys2);
                foreach (var k in allKeys)
                {
                    if (!_subscribers.ContainsKey(k) && !_newSubscribers.ContainsKey(k))
                        _subscriptionKeys = _subscriptionKeys.Remove(k);
                }
            }
        }

        private void ReceiveMemberUp(Member m)
        {
            if (MatchingRole(m) && m.Address != _selfAddress)
                _nodes = _nodes.Add(m.Address);
        }

        private void ReceiveMemberRemoved(Member m)
        {
            if (m.Address == _selfAddress) Context.Stop(Self);
            else if (MatchingRole(m))
            {
                _nodes = _nodes.Remove(m.Address);
                _log.Debug("Adding removed node [{0}] from MemberRemoved", m.UniqueAddress);
                _removedNodes = _removedNodes.SetItem(m.UniqueAddress, _allReachableClockTime);
                _unreachable = _unreachable.Remove(m.Address);
            }
        }

        private void ReceiveUnreachable(Member m)
        {
            if (MatchingRole(m))
            {
                _unreachable = _unreachable.Add(m.Address);
            }
        }

        private void ReceiveReachable(Member m)
        {
            if (MatchingRole(m))
            {
                _unreachable = _unreachable.Remove(m.Address);
            }
        }

        private void ReceiveLeaderChanged(Address leader, string role)
        {
            if (role == _settings.Role) _leader = leader;
        }

        private void ReceiveClockTick()
        {
            var now = DateTime.UtcNow.Ticks * 100;
            if (_unreachable.Count == 0)
            {
                _allReachableClockTime += (now - _previousClockTime);
            }
            _previousClockTime = now;
        }

        private void ReceiveRemovedNodePruningTick()
        {
            if (IsLeader && _removedNodes.Any())
                InitRemovedNodePruning();

            PerformRemovedNodePruning();
            TombstoneRemovedNodePruning();
        }

        private bool AllPruningPerformed(UniqueAddress removed)
        {
            return _dataEntries.All(x =>
                {
                    var key = x.Key;
                    var envelope = x.Value.Item1;
                    var data = envelope.Data;
                    var pruning = envelope.Pruning;
                    if (data is IRemovedNodePruning)
                    {
                        var z = pruning[removed];
                        return (z != null) && !(z.Phase is PruningInitialized);
                    }
                    return false;
                });
        }

        private void TombstoneRemovedNodePruning()
        {
            foreach (var performed in _pruningPerformed)
            {
                var removed = performed.Key;
                var timestamp = performed.Value;
                if ((_allReachableClockTime - timestamp) > _maxPruningDisseminationNanos && AllPruningPerformed(removed))
                {
                    _log.Debug("All pruning performed for {0}, tombstoned", removed);
                    _pruningPerformed = _pruningPerformed.Remove(removed);
                    _removedNodes = _removedNodes.Remove(removed);
                    _tombstonedNodes = _tombstonedNodes.Add(removed);

                    foreach (var entry in _dataEntries)
                    {
                        var key = entry.Key;
                        var envelope = entry.Value.Item1;
                        var pruning = envelope.Data as IRemovedNodePruning;
                        if (pruning != null)
                        {
                            SetData(key, PruningCleanupTombstoned(envelope, removed));
                        }
                    }
                }
            }
        }

        private void PerformRemovedNodePruning()
        {
            // perform pruning when all seen Init
            var allNodes = _nodes; //TODO: _nodes.Union(_weaklyUpNodes)
            var prunningPerformed = new PruningPerformed(DateTime.UtcNow + _settings.PruningMarkerTimeToLive);
            var durablePrunningPerformed = new PruningPerformed(DateTime.UtcNow + _settings.DurablePruningMarkerTimeToLive);

            foreach (var entry in _dataEntries)
            {
                var key = entry.Key;
                var envelope = entry.Value.Item1;
                var data = envelope.Data as IRemovedNodePruning;
                if (data != null)
                {
                    foreach (var entry2 in envelope.Pruning)
                    {
                        var init = entry2.Value as PruningInitialized;
                        if (init != null && init.Owner == _selfUniqueAddress && (allNodes.IsEmpty || allNodes.IsSubsetOf(init.Seen)))
                        {
                            var removed = entry2.Key;
                            var isDurable = IsDurable(key);
                            var newEnvelope = envelope.Prune(removed, isDurable ? durablePrunningPerformed : prunningPerformed);
                            _log.Debug("Perform pruning of [{0}] from [{1}] to [{2}]", key, removed, _selfUniqueAddress);
                            SetData(key, newEnvelope);

                            if (!ReferenceEquals(newEnvelope.Data, data) && isDurable)
                            {
                                _durableStore.Tell(new Store(key, new DurableDataEnvelope(newEnvelope)));
                            }
                        }
                    }
                }
            }
        }

        private DataEnvelope PruningCleanupTombstoned(DataEnvelope envelope)
        {
            return _tombstonedNodes.Aggregate(envelope, PruningCleanupTombstoned);
        }

        private DataEnvelope PruningCleanupTombstoned(DataEnvelope envelope, UniqueAddress removed)
        {
            var pruningCleanedup = PruningCleanupTombstoned(removed, envelope.Data);
            return (pruningCleanedup != envelope.Data) || envelope.Pruning.ContainsKey(removed)
                ? new DataEnvelope(pruningCleanedup, envelope.Pruning.Remove(removed))
                : envelope;
        }

        private IReplicatedData PruningCleanupTombstoned(IReplicatedData data)
        {
            if (_tombstonedNodes.IsEmpty)
            {
                return data;
            }
            else
            {
                return _tombstonedNodes.Aggregate(data, (d, removed) => PruningCleanupTombstoned(removed, d));
            }
        }

        private IReplicatedData PruningCleanupTombstoned(UniqueAddress removed, IReplicatedData data)
        {
            var d = data as IRemovedNodePruning;
            if (d != null)
            {
                if (d.NeedPruningFrom(removed))
                {
                    return d.PruningCleanup(removed);
                }
            }
            return data;
        }

        private void InitRemovedNodePruning()
        {
            var removedSet = _removedNodes.Where(x => (_allReachableClockTime - x.Value) > _maxPruningDisseminationNanos)
                                          .Select(x => x.Key)
                                          .ToImmutableHashSet();

            if (!removedSet.IsEmpty)
            {
                foreach (var entry in _dataEntries)
                {
                    var key = entry.Key;
                    var envelope = entry.Value.Item1;

                    foreach (var removed in removedSet)
                    {
                        Action init = () =>
                            {
                                var newEnvelope = envelope.InitRemovedNodePruning(removed, _selfUniqueAddress);
                                Context.System.Log.Debug("Initiating pruning of {0} with data {1}", removed, key);
                                SetData(key, newEnvelope);
                            };
                        if (envelope.NeedPruningFrom(removed))
                        {
                            if (envelope.Data is IRemovedNodePruning)
                            {
                                var res = envelope.Pruning[removed];
                                if (res == null)
                                {
                                    init();
                                }
                                else if (res.Phase is PruningInitialized && res.Owner == _selfUniqueAddress)
                                {
                                    init();
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ReceiveGetReplicaCount() => Sender.Tell(new ReplicaCount(_nodes.Count + 1));
    }
}
