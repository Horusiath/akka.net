using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using Akka.Actor;
using Akka.Eventsourced.EventsourcedProtocol;

namespace Akka.Eventsourced.Logs
{
    internal struct SubscriberRegistry
    {
        public static readonly SubscriberRegistry Empty = new SubscriberRegistry(Logs.AggregateRegistry.Empty, ImmutableHashSet<IActorRef>.Empty);

        public readonly AggregateRegistry AggregateRegistry;
        public readonly IImmutableSet<IActorRef> DefaultRegistry;

        public SubscriberRegistry(IImmutableSet<IActorRef> defaultRegistry) : this(new AggregateRegistry(), defaultRegistry)
        {
        }

        public SubscriberRegistry(AggregateRegistry aggregateRegistry, IImmutableSet<IActorRef> defaultRegistry = null)
        {
            AggregateRegistry = aggregateRegistry;
            DefaultRegistry = defaultRegistry ?? ImmutableHashSet<IActorRef>.Empty;
        }

        public SubscriberRegistry RegisterDefaultSubscriber(IActorRef subscriber)
        {
            return new SubscriberRegistry(
                defaultRegistry: DefaultRegistry.Add(subscriber));
        }

        public SubscriberRegistry RegisterAggregateSubscriber(IActorRef subscriber, string aggregateId)
        {
            return new SubscriberRegistry(
                aggregateRegistry: AggregateRegistry.Add(subscriber, aggregateId));
        }

        public SubscriberRegistry UnregisterSubscriber(IActorRef subscriber)
        {
            var aggregateId = AggregateRegistry.GetAggregateId(subscriber);
            if (aggregateId == null) return new SubscriberRegistry(defaultRegistry: DefaultRegistry.Remove(subscriber));
            else return new SubscriberRegistry(aggregateRegistry: AggregateRegistry.Remove(subscriber, aggregateId));
        }

        public void PushReplicateSuccess(IEnumerable<DurableEvent> events)
        {
            foreach (var durableEvent in events)
            {
                var written = new Written(durableEvent);
                foreach (var actorRef in DefaultRegistry)
                    actorRef.Tell(written);

                foreach (var aggregateId in durableEvent.DestinationAggregateId)
                    foreach (var aggregate in AggregateRegistry[aggregateId])
                        aggregate.Tell(written);
            }
        }

        public void PushWriteSuccess(IEnumerable<DurableEvent> events, IActorRef initiator, IActorRef requestor, int instanceId)
        {
            foreach (var durableEvent in events)
            {
                requestor.Tell(new WriteSuccess(durableEvent, instanceId));
                var written = new Written(durableEvent);

                foreach (var actorRef in DefaultRegistry)
                    if (!Equals(actorRef, requestor))
                        actorRef.Tell(written);

                foreach (var aggregateId in durableEvent.DestinationAggregateId)
                    foreach (var aggregate in AggregateRegistry[aggregateId])
                        if (!Equals(aggregate, requestor))
                            aggregate.Tell(written);
            }
        }

        public void PushWriteFailure(IEnumerable<DurableEvent> events, IActorRef initiator, IActorRef requestor, int instanceId, Exception cause)
        {
            foreach (var durableEvent in events)
                requestor.Tell(new WriteFailure(durableEvent, cause, instanceId), initiator);
        }
    }

    internal struct AggregateRegistry
    {
        public static AggregateRegistry Empty = new AggregateRegistry();

        public readonly IImmutableDictionary<string, IImmutableSet<IActorRef>> Registry;
        public readonly IImmutableDictionary<IActorRef, string> RegistryIndex;

        public AggregateRegistry(IImmutableDictionary<string, IImmutableSet<IActorRef>> registry = null, IImmutableDictionary<IActorRef, string> registryIndex = null)
        {
            Registry = registry ?? ImmutableDictionary<string, IImmutableSet<IActorRef>>.Empty;
            RegistryIndex = registryIndex ?? ImmutableDictionary<IActorRef, string>.Empty;
        }

        public IImmutableSet<IActorRef> this[string aggregateId]
        {
            get { return GetAggregates(aggregateId); }
        }

        [Pure]
        public string GetAggregateId(IActorRef aggregate)
        {
            string aggregateId;
            return RegistryIndex.TryGetValue(aggregate, out aggregateId) ? aggregateId : null;
        }

        [Pure]
        public IImmutableSet<IActorRef> GetAggregates(string aggregateId)
        {
            IImmutableSet<IActorRef> aggregates;
            return Registry.TryGetValue(aggregateId, out aggregates) ? aggregates : ImmutableHashSet<IActorRef>.Empty;
        }

        [Pure]
        public AggregateRegistry Add(IActorRef aggregate, string aggregateId)
        {
            IImmutableSet<IActorRef> aggregates;
            if (!Registry.TryGetValue(aggregateId, out aggregates))
                aggregates = ImmutableHashSet<IActorRef>.Empty;

            return new AggregateRegistry(
                registry: Registry.SetItem(aggregateId, aggregates.Add(aggregate)),
                registryIndex: RegistryIndex.SetItem(aggregate, aggregateId));
        }

        [Pure]
        public AggregateRegistry Remove(IActorRef aggregate, string aggregateId)
        {
            IImmutableSet<IActorRef> aggregates;
            if (!Registry.TryGetValue(aggregateId, out aggregates))
                aggregates = ImmutableHashSet<IActorRef>.Empty;

            return new AggregateRegistry(
                registry: Registry.SetItem(aggregateId, aggregates.Remove(aggregate)),
                registryIndex: RegistryIndex.Remove(aggregate));
        }
    }
}