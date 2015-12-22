using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Eventsourced.Logs;

namespace Akka.Eventsourced.ReplicationProtocol
{
    public interface IReplicationSerializable { }

    [Serializable]
    public struct ReplicationEndpointInfo : IReplicationSerializable
    {
        public static string LogId(string endpointId, string logName)
        {
            return endpointId + "_" + logName;
        }

        public readonly string EndpointId;
        public readonly IImmutableSet<string> LogNames;

        public ReplicationEndpointInfo(string endpointId, IImmutableSet<string> logNames) : this()
        {
            EndpointId = endpointId;
            LogNames = logNames;
        }

        public string LogId(string logName)
        {
            return LogId(EndpointId, logName);
        }
    }

    [Serializable]
    public sealed class GetReplicationEndpointInfo : IReplicationSerializable
    {
        public static readonly GetReplicationEndpointInfo Instance = new GetReplicationEndpointInfo();

        private GetReplicationEndpointInfo()
        {
        }
    }

    [Serializable]
    public struct GetReplicationEndpointInfoSuccess : IReplicationSerializable
    {
        public readonly ReplicationEndpointInfo Info;

        public GetReplicationEndpointInfoSuccess(ReplicationEndpointInfo info) : this()
        {
            Info = info;
        }
    }

    [Serializable]
    public sealed class ReplicationDue : IReplicationSerializable
    {
        public static readonly ReplicationDue Instance = new ReplicationDue();

        private ReplicationDue()
        {
        }
    }

    [Serializable]
    public sealed class GetTimeTracker
    {
        public static readonly GetTimeTracker Instance = new GetTimeTracker();

        private GetTimeTracker()
        {
        }
    }

    [Serializable]
    public struct GetTimeTrackerSuccess
    {
        public readonly TimeTracker Tracker;

        public GetTimeTrackerSuccess(TimeTracker tracker)
        {
            Tracker = tracker;
        }
    }

    [Serializable]
    public sealed class GetReplicationProgresses
    {
        public static readonly GetReplicationProgresses Instance = new GetReplicationProgresses();

        private GetReplicationProgresses()
        {
        }
    }

    [Serializable]
    public struct GetReplicationProgressesSuccess
    {
        public readonly IImmutableDictionary<string, long> Progresses;

        public GetReplicationProgressesSuccess(IImmutableDictionary<string, long> progresses)
        {
            Progresses = progresses;
        }
    }

    [Serializable]
    public struct GetReplicationProgressesFailure
    {
        public readonly Exception Cause;

        public GetReplicationProgressesFailure(Exception cause)
        {
            Cause = cause;
        }
    }

    [Serializable]
    public struct GetReplicationProgress
    {
        public readonly string SourceLogId;

        public GetReplicationProgress(string sourceLogId)
        {
            SourceLogId = sourceLogId;
        }
    }

    [Serializable]
    public struct GetReplicationProgressSuccess
    {
        public readonly IImmutableDictionary<string, long> Progresses;

        public GetReplicationProgressSuccess(IImmutableDictionary<string, long> progresses)
        {
            Progresses = progresses;
        }
    }

    [Serializable]
    public struct GetReplicationProgressFailure
    {
        public readonly Exception Cause;

        public GetReplicationProgressFailure(Exception cause)
        {
            Cause = cause;
        }
    }

    [Serializable]
    public struct SetReplicationProgress
    {
        public readonly string SourceLogId;
        public readonly long ReplicationProgress;

        public SetReplicationProgress(string sourceLogId, long replicationProgress)
        {
            SourceLogId = sourceLogId;
            ReplicationProgress = replicationProgress;
        }
    }

    [Serializable]
    public struct SetReplicationProgressSuccess
    {
        public readonly string SourceLogId;
        public readonly long StoredReplicationProgress;

        public SetReplicationProgressSuccess(string sourceLogId, long storedReplicationProgress)
        {
            SourceLogId = sourceLogId;
            StoredReplicationProgress = storedReplicationProgress;
        }
    }

    [Serializable]
    public struct SetReplicationProgressFailure
    {
        public readonly Exception Cause;

        public SetReplicationProgressFailure(Exception cause)
        {
            Cause = cause;
        }
    }

    [Serializable]
    public struct ReplicationReadEnvelope : IReplicationSerializable
    {
        public readonly ReplicationRead Payload;
        public readonly string LogName;

        public ReplicationReadEnvelope(ReplicationRead payload, string logName) : this()
        {
            Payload = payload;
            LogName = logName;
        }
    }

    [Serializable]
    public struct ReplicationRead : IReplicationSerializable
    {
        public readonly long FromSequenceNr;
        public readonly int MaxNumberOfEvents;
        public readonly IReplicationFilter Filter;
        public readonly string TargetLogId;
        public readonly IActorRef Replicator;
        public readonly VectorTime CurrentTargetVectorTime;

        public ReplicationRead(long fromSequenceNr, int maxNumberOfEvents, IReplicationFilter filter, string targetLogId, IActorRef replicator, VectorTime currentTargetVectorTime)
        {
            FromSequenceNr = fromSequenceNr;
            MaxNumberOfEvents = maxNumberOfEvents;
            Filter = filter;
            TargetLogId = targetLogId;
            Replicator = replicator;
            CurrentTargetVectorTime = currentTargetVectorTime;
        }
    }

    [Serializable]
    public struct ReplicationReadSuccess : IReplicationSerializable
    {
        public readonly IEnumerable<DurableEvent> Events;
        public readonly long ReplicationProgress;
        public readonly string TargetLogId;
        public readonly VectorTime CurrentSourceVectorTime;

        public ReplicationReadSuccess(IEnumerable<DurableEvent> events, long replicationProgress, string targetLogId, VectorTime currentSourceVectorTime)
        {
            Events = events;
            ReplicationProgress = replicationProgress;
            TargetLogId = targetLogId;
            CurrentSourceVectorTime = currentSourceVectorTime;
        }
    }

    [Serializable]
    public struct ReplicationReadFailure : IReplicationSerializable
    {
        public readonly Exception Cause;
        public readonly string TargetLogId;

        public ReplicationReadFailure(Exception cause, string targetLogId)
        {
            Cause = cause;
            TargetLogId = targetLogId;
        }
    }

    [Serializable]
    public struct ReplicationWrite : IReplicationSerializable
    {
        public readonly IEnumerable<DurableEvent> Events;
        public readonly string SourceLogId;
        public readonly long ReplicationProgress;
        public readonly VectorTime CurrentSourceVectorTime;

        public ReplicationWrite(IEnumerable<DurableEvent> events, string sourceLogId, long replicationProgress, VectorTime currentSourceVectorTime) : this()
        {
            Events = events;
            SourceLogId = sourceLogId;
            ReplicationProgress = replicationProgress;
            CurrentSourceVectorTime = currentSourceVectorTime;
        }
    }

    [Serializable]
    public struct ReplicationWriteSuccess : IReplicationSerializable
    {
        public readonly int Num;
        public readonly long StoredReplicationProgress;
        public readonly VectorTime CurrentTargetVectorTime;

        public ReplicationWriteSuccess(int num, long storedReplicationProgress, VectorTime currentTargetVectorTime) : this()
        {
            Num = num;
            StoredReplicationProgress = storedReplicationProgress;
            CurrentTargetVectorTime = currentTargetVectorTime;
        }
    }

    [Serializable]
    public struct ReplicationWriteFailure : IReplicationSerializable
    {
        public readonly Exception Cause;

        public ReplicationWriteFailure(Exception cause)
        {
            Cause = cause;
        }
    }

}