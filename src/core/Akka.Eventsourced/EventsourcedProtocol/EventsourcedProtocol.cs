using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;

namespace Akka.Eventsourced.EventsourcedProtocol
{
    [Serializable]
    public struct WriteMany
    {
        public readonly IEnumerable<Write> Writes;

        public WriteMany(IEnumerable<Write> writes)
        {
            Writes = writes;
        }
    }

    [Serializable]
    public sealed class WriteManyComplete
    {
        public static readonly WriteManyComplete Instance = new WriteManyComplete();

        private WriteManyComplete()
        {
        }
    }

    [Serializable]
    public struct Write : IDurableEventBatch
    {
        public readonly IActorRef Initiator;
        public readonly IActorRef Requestor;
        public readonly long InstanceId;

        public Write(IEnumerable<DurableEvent> events, IActorRef initiator, IActorRef requestor, long instanceId) : this()
        {
            Events = events;
            Initiator = initiator;
            Requestor = requestor;
            InstanceId = instanceId;
        }

        public int Count { get { return Events.Count(); } }
        public IEnumerable<DurableEvent> Events { get; private set; }
    }

    [Serializable]
    public struct WriteSuccess
    {
        public readonly DurableEvent Event;
        public readonly long InstanceId;

        public WriteSuccess(DurableEvent @event, long instanceId) : this()
        {
            Event = @event;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct WriteFailure
    {
        public readonly DurableEvent Event;
        public readonly Exception Cause;
        public readonly long InstanceId;

        public WriteFailure(DurableEvent @event, Exception cause, long instanceId)
        {
            Event = @event;
            Cause = cause;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct Written
    {
        public readonly DurableEvent Event;

        public Written(DurableEvent @event)
        {
            Event = @event;
        }
    }

    [Serializable]
    public struct Replay
    {
        public readonly long From;
        public readonly int Max;
        public readonly IActorRef Requestor;
        public readonly string AggregateId;
        public readonly long InstanceId;

        public Replay(long @from, int max, IActorRef requestor, string aggregateId, long instanceId) : this()
        {
            From = @from;
            Max = max;
            Requestor = requestor;
            AggregateId = aggregateId;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct ReplayNext
    {
        public readonly int Max;
        public readonly long InstanceId;

        public ReplayNext(int max, long instanceId)
        {
            Max = max;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct Replaying
    {
        public readonly DurableEvent Event;
        public readonly long InstanceId;

        public Replaying(DurableEvent @event, long instanceId)
        {
            Event = @event;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct ReplaySuspended
    {
        public readonly long InstanceId;

        public ReplaySuspended(long instanceId)
        {
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct ReplaySuccess
    {
        public readonly long InstanceId;

        public ReplaySuccess(long instanceId)
        {
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct ReplayFailure
    {
        public readonly Exception Cause;
        public readonly long InstanceId;

        public ReplayFailure(Exception cause, long instanceId)
        {
            Cause = cause;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct SaveSnapshot
    {
        public readonly Snapshot Snapshot;
        public readonly IActorRef Initiator;
        public readonly IActorRef Requestor;
        public readonly long InstanceId;

        public SaveSnapshot(Snapshot snapshot, IActorRef initiator, IActorRef requestor, long instanceId)
        {
            Snapshot = snapshot;
            Initiator = initiator;
            Requestor = requestor;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct SaveSnapshotSuccess
    {
        public readonly SnapshotMetadata Metadata;
        public readonly long InstanceId;

        public SaveSnapshotSuccess(SnapshotMetadata metadata, long instanceId) : this()
        {
            Metadata = metadata;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct SaveSnapshotFailure
    {
        public readonly SnapshotMetadata Metadata;
        public readonly Exception Cause;
        public readonly long InstanceId;

        public SaveSnapshotFailure(SnapshotMetadata metadata, Exception cause, long instanceId)
        {
            Metadata = metadata;
            Cause = cause;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct LoadSnapshot
    {
        public readonly string EmitterId;
        public readonly IActorRef Requestor;
        public readonly long InstanceId;

        public LoadSnapshot(string emitterId, IActorRef requestor, long instanceId) : this()
        {
            EmitterId = emitterId;
            Requestor = requestor;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct LoadSnapshotSuccess
    {
        public readonly Snapshot Snapshot;
        public readonly long InstanceId;

        public LoadSnapshotSuccess(Snapshot snapshot, long instanceId) : this()
        {
            Snapshot = snapshot;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct LoadSnapshotFailure
    {
        public readonly Exception Cause;
        public readonly long InstanceId;

        public LoadSnapshotFailure(Exception cause, long instanceId)
        {
            Cause = cause;
            InstanceId = instanceId;
        }
    }

    [Serializable]
    public struct DeleteSnapshots
    {
        public readonly long LowerSequenceNr;

        public DeleteSnapshots(long lowerSequenceNr)
        {
            LowerSequenceNr = lowerSequenceNr;
        }
    }

    [Serializable]
    public sealed class DeleteSnapshotsSuccess
    {
        public static readonly DeleteSnapshotsSuccess Instance = new DeleteSnapshotsSuccess();
        private DeleteSnapshotsSuccess() { }
    }

    [Serializable]
    public struct DeleteSnapshotsFailure
    {
        public readonly Exception Cause;

        public DeleteSnapshotsFailure(Exception cause)
        {
            Cause = cause;
        }
    }

}