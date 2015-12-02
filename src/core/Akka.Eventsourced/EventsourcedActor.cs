using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Eventsourced.EventsourcedProtocol;
using Akka.Util;

namespace Akka.Eventsourced
{
    public abstract class EventsourcedActor : EventsourcedView
    {
        private IImmutableList<DurableEvent> writeRequests = ImmutableList<DurableEvent>.Empty;
        private IImmutableList<Handler<object>> writeHandlers = ImmutableList<Handler<object>>.Empty;
        private bool isWriting = false;

        public virtual bool IsStateSync { get { return true; } }
        public bool IsWritePending { get { return writeRequests.Count != 0; } }

        public void PersistN<T>(IEnumerable<T> events, Handler<T> handler, Action<T> onLast = null,
            IImmutableSet<string> customDestinationAggregateIds = null)
        {
            var last = default(T);
            foreach (var e in events)
            {
                last = e;
                Persist(e, handler, customDestinationAggregateIds);
            }

            if (onLast != null && Equals(last, default(T)))
                onLast(last);
        }

        private void Persist<T>(T e, Handler<T> handler, IImmutableSet<string> customDestinationAggregateIds = null)
        {
            writeRequests = writeRequests.Add(DurableEvent(e, customDestinationAggregateIds ?? ImmutableHashSet<string>.Empty));
            writeHandlers = writeHandlers.Add(result => handler(result.IsSuccess ? Result.Success((T)Convert.ChangeType(result.Value, typeof(T))) : Result.Failure<T>(result.Exception)));
        }

        public void Write()
        {
            EventLog.Tell(new Write(writeRequests, Sender, Self, InstanceId));
            writeRequests = ImmutableList<DurableEvent>.Empty;
        }

        private DurableEvent DurableEvent(object payload, IImmutableSet<string> customDestinationAggregateIds)
        {
            if (HasSharedClockEntry)
            {
                return new DurableEvent(
                    payload: payload,
                    emitterId: Id,
                    emitterAggregateId: AggregateId,
                    customDestinationAggregateIds: customDestinationAggregateIds,
                    vectorTimestamp: CurrentTime,
                    processId: string.Empty);
            }
            else
            {
                return new DurableEvent(
                    payload: payload,
                    emitterId: Id,
                    emitterAggregateId: AggregateId,
                    customDestinationAggregateIds: customDestinationAggregateIds,
                    systemTimestamp: DateTime.UtcNow.Ticks,
                    vectorTimestamp: IncrementLocalTime(),
                    processId: Id);
            }
        }

        protected override void UnhandedMessage(object message)
        {
            message.Match()
                .With<WriteSuccess>(success =>
                {
                    if (success.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        OnEventInternal(success.Event);
                        ConditionChanged(LastHandledEvent.VectorTimestamp);
                        writeHandlers[0](Result.Success(success.Event.Payload));
                        writeHandlers = writeHandlers.RemoveAt(0);
                        if (IsStateSync && writeHandlers.Count == 0)
                        {
                            isWriting = false;
                            Stash.Unstash();
                        }
                    }
                })
                .With<WriteFailure>(failure =>
                {
                    if (failure.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        OnEventInternal(failure.Event, failure.Cause);
                        writeHandlers[0](Result.Failure<object>(failure.Cause));
                        writeHandlers = writeHandlers.RemoveAt(0);
                        if (IsStateSync && writeHandlers.Count == 0)
                        {
                            isWriting = false;
                            Stash.Unstash();
                        }
                    }
                })
                .Default(command =>
                {
                    if (isWriting) Stash.Stash();
                    else
                    {
                        OnCommand(command);
                        var writePending = IsWritePending;

                        if (writePending) Write();

                        if (writePending && IsStateSync) isWriting = true;
                        else if (IsStateSync) Stash.Unstash();
                    }
                });
        }
    }
}