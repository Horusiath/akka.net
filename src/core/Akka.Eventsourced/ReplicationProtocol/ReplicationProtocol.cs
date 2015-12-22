using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Eventsourced.Logs;

namespace Akka.Eventsourced.ReplicationProtocol
{
    public interface IReplicationSerializable { }

    [Serializable]
    public struct LogInfo : IReplicationSerializable
    {
        public readonly string LogName;
        public readonly long SequenceNr;

        public LogInfo(string logName, long sequenceNr)
        {
            LogName = logName;
            SequenceNr = sequenceNr;
        }
    }

    [Serializable]
    public struct ReplicationEndpointInfo : IReplicationSerializable
    {
        public static string LogId(string endpointId, string logName)
        {
            return endpointId + "_" + logName;
        }

        public readonly string EndpointId;
        public readonly IImmutableSet<LogInfo> LogInfos;

        public ReplicationEndpointInfo(string endpointId, IImmutableSet<LogInfo> logInfos) : this()
        {
            EndpointId = endpointId;
            LogInfos = logInfos;
        }

        public string LogId(string logName)
        {
            return LogId(EndpointId, logName);
        }

        public LogInfo? LogInfo(string logName)
        {
            var logInfo = LogInfos.FirstOrDefault(x => x.LogName == logName);
            return Equals(logInfo, default(LogInfo))
                ? default(LogInfo?)
                : logInfo;
        }

        public IEnumerable<string> LogNames { get { return LogInfos.Select(x => x.LogName); } }
    }

    [Serializable]
    internal sealed class GetReplicationEndpointInfo : IReplicationSerializable
    {
        public static readonly GetReplicationEndpointInfo Instance = new GetReplicationEndpointInfo();

        private GetReplicationEndpointInfo()
        {
        }
    }

    [Serializable]
    internal struct GetReplicationEndpointInfoSuccess : IReplicationSerializable
    {
        public readonly ReplicationEndpointInfo Info;

        public GetReplicationEndpointInfoSuccess(ReplicationEndpointInfo info) : this()
        {
            Info = info;
        }
    }

    [Serializable]
    internal sealed class ReplicationDue : IReplicationSerializable
    {
        public static readonly ReplicationDue Instance = new ReplicationDue();

        private ReplicationDue()
        {
        }
    }

    [Serializable]
    public sealed class GetEventLogClock
    {
        public static readonly GetEventLogClock Instance = new GetEventLogClock();

        private GetEventLogClock()
        {
        }
    }

    [Serializable]
    public struct GetEventLogClockSucces
    {
        public readonly EventLogClock Clock;

        public GetEventLogClockSucces(EventLogClock clock)
        {
            Clock = clock;
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
    public struct ReplicationReadSuccess : IDurableEventBatch, IReplicationSerializable
    {
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

        public int Count { get { return Events.Count(); } }
        public IEnumerable<DurableEvent> Events { get; private set; }
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
    public struct ReplicationWriteMany
    {
        public readonly IEnumerable<ReplicationWrite> Writes;

        public ReplicationWriteMany(IEnumerable<ReplicationWrite> writes)
        {
            Writes = writes;
        }
    }

    [Serializable]
    public sealed class ReplicationWriteManyComplete
    {
        public static readonly ReplicationWriteManyComplete Instance = new ReplicationWriteManyComplete();

        private ReplicationWriteManyComplete()
        {
        }
    }

    [Serializable]
    public struct ReplicationWrite : IDurableEventBatch, IReplicationSerializable
    {
        public readonly string SourceLogId;
        public readonly long ReplicationProgress;
        public readonly VectorTime CurrentSourceVectorTime;
        public readonly IActorRef Initiator;

        public ReplicationWrite(IEnumerable<DurableEvent> events, string sourceLogId, long replicationProgress, VectorTime currentSourceVectorTime, IActorRef initiator = null) : this()
        {
            Events = events;
            SourceLogId = sourceLogId;
            ReplicationProgress = replicationProgress;
            CurrentSourceVectorTime = currentSourceVectorTime;
            Initiator = initiator;
        }

        public ReplicationWrite WithInitiator(IActorRef initiator)
        {
            if (initiator == null) throw new ArgumentNullException("initiator", "ReplicationWrite.WithInitiator expected initiator to be not null");
            return new ReplicationWrite(Events, SourceLogId, ReplicationProgress, CurrentSourceVectorTime, initiator);
        }

        public int Count { get { return Events.Count(); } }
        public IEnumerable<DurableEvent> Events { get; private set; }
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