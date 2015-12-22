using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Eventsourced.Logs;
using Akka.Eventsourced.ReplicationProtocol;

namespace Akka.Eventsourced
{
    [Serializable]
    public sealed class RecoverySettings
    {
        public static RecoverySettings Create(ActorSystem system)
        {
            return Create(system.Settings.Config.GetConfig("akka.eventsourced.disaster-recovery"));
        }

        public static RecoverySettings Create(Config config)
        {
            return new RecoverySettings(
                maxRemoteOperationRetries: config.GetInt("max-remote-operation-retries"),
                remoteOperationRetryDelay: config.GetTimeSpan("remote-operaton-retry-delay"),
                remoteOperationTimeout: config.GetTimeSpan("remote-operation-timeout"),
                snapshotDeletionTimeout: config.GetTimeSpan("snapshot-deletion-timeout"));
        }

        public readonly int MaxRemoteOperationRetries;
        public readonly TimeSpan RemoteOperationRetryDelay;
        public readonly TimeSpan RemoteOperationTimeout;
        public readonly TimeSpan SnapshotDeletionTimeout;

        public RecoverySettings(int maxRemoteOperationRetries, TimeSpan remoteOperationRetryDelay, TimeSpan remoteOperationTimeout, TimeSpan snapshotDeletionTimeout)
        {
            MaxRemoteOperationRetries = maxRemoteOperationRetries;
            RemoteOperationRetryDelay = remoteOperationRetryDelay;
            RemoteOperationTimeout = remoteOperationTimeout;
            SnapshotDeletionTimeout = snapshotDeletionTimeout;
        }
    }

    [Serializable]
    internal struct RecoveryLink
    {
        public readonly string LogName;
        public readonly EventLogClock LocalClock;
        public readonly string RemoteLogId;
        public readonly long RemoteSequenceNr;

        public RecoveryLink(string logName, EventLogClock localClock, string remoteLogId, long remoteSequenceNr)
        {
            LogName = logName;
            LocalClock = localClock;
            RemoteLogId = remoteLogId;
            RemoteSequenceNr = remoteSequenceNr;
        }
    }
    
    internal class Recovery
    {
        public static async Task<T> Retry<T>(Func<Task<T>> asyncFunc, TimeSpan delay, int retries)
        {
            throw new NotImplementedException();
        }

        public readonly RecoverySettings Settings;
        public readonly ReplicationEndpoint ReplicationEndpoint;

        public Recovery(ReplicationEndpoint replicationEndpoint)
        {
            ReplicationEndpoint = replicationEndpoint;
            Settings = RecoverySettings.Create(replicationEndpoint.System);
        }

        public async Task<IImmutableDictionary<string, EventLogClock>> ReadEventLogClocksAsync()
        {
            throw new NotImplementedException();
        }

        public async Task<IImmutableSet<ReplicationEndpointInfo>> ReadEndpointInfoAsync()
        {
            throw new NotImplementedException();
        }

        public async Task DeleteSnapshotsAsync(RecoveryLink link)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<RecoveryLink> GetRecoveryLinks(
            IImmutableSet<ReplicationEndpointInfo> endpointInfos,
            IImmutableDictionary<string, EventLogClock> EventLogClocks)
        {
            throw new NotImplementedException();
        } 
    }

    internal class Acceptor : ReceiveActor
    {
        #region messages

        [Serializable]
        public sealed class Process
        {
            public static readonly Process Instance = new Process();

            private Process()
            {
            }
        }
        
        public struct Recover
        {
            public readonly IImmutableSet<RecoveryLink> Links;
            public readonly TaskCompletionSource<object> Promise;

            public Recover(IImmutableSet<RecoveryLink> links, TaskCompletionSource<object> promise)
            {
                Links = links;
                Promise = promise;
            }
        }

        [Serializable]
        public struct RecoveryStepCompleted
        {
            public readonly RecoveryLink Link;

            public RecoveryStepCompleted(RecoveryLink link)
            {
                Link = link;
            }
        }

        [Serializable]
        public sealed class RecoveryCompleted
        {
            public static readonly RecoveryCompleted Instance = new RecoveryCompleted();

            private RecoveryCompleted()
            {
            }
        }
        #endregion

        public const string Name = "acceptor";
        private readonly ReplicationEndpoint _endpoint;

        public Acceptor(ReplicationEndpoint endpoint)
        {
            _endpoint = endpoint;

            Initializing();
        }

        private ILoggingAdapter _log;
        public ILoggingAdapter Log { get { return _log ?? (_log = Context.GetLogger()); } }
        
        private void Initializing()
        {
            Receive<Process>(_ => Become(Processing));
            Receive<Recover>(recover =>
            {
                Log.Debug("Recovery of [{0}]. Checking replication progress with remote endpoints...", _endpoint.Id);
                var manager = Context.ActorOf(Props.Create(() => new RecoveryManager(_endpoint.Id, recover.Links)));
                Become(Recovering(manager, recover.Promise));
            });
        }

        private Action Recovering(IActorRef recovery, TaskCompletionSource<object> promise)
        {
            return () =>
            {
                Receive<ReplicationReadEnvelope>(re => recovery.Forward(re));
                Receive<RecoveryCompleted>(_ =>
                {
                    promise.SetResult(null);
                    Become(Processing);
                });
            };
        }

        private void Processing()
        {
            Receive<ReplicationReadEnvelope>(envelope => _endpoint.Logs[envelope.LogName].Forward(envelope.Payload));
        }

        protected override void Unhandled(object message)
        {
            if (message is ReplicationEndpointInfo)
            {
                Sender.Tell(new GetReplicationEndpointInfoSuccess(_endpoint.Info));
            }
            else base.Unhandled(message);
        }
    }

    internal class RecoveryManager : ReceiveActor
    {
        public RecoveryManager(string endpointId, IImmutableSet<RecoveryLink> links)
        {
            var log = Context.GetLogger();
            var active = links;
            var compensators = links
                .ToImmutableDictionary(
                    link => link.RemoteLogId,
                    link => Context.ActorOf(Props.Create(() => new RecoveryActor(endpointId, link))));

            Receive<ReplicationReadEnvelope>(
                envelope => compensators[envelope.Payload.TargetLogId].Forward(envelope.Payload));
            Receive<Acceptor.RecoveryStepCompleted>(completed => active.Contains(completed.Link), completed =>
            {
                active = active.Remove(completed.Link);
                var progress = links.Count - active.Count;
                var all = links.Count;

                log.Debug("[Recovery of {0}] Confirm existence of consistent replication progress at {1} ({2} of {3}).", 
                    endpointId, completed.Link.RemoteLogId, progress, all);

                if(active.Count == 0) Context.Parent.Tell(Acceptor.RecoveryCompleted.Instance);
            });
        }
    }

    internal class RecoveryActor : ReceiveActor
    {
        private readonly string endpointId;
        private readonly RecoveryLink link;
        private readonly ILoggingAdapter log;

        public RecoveryActor(string endpointId, RecoveryLink link)
        {
            this.endpointId = endpointId;
            this.link = link;
            this.log = Context.GetLogger();

            RecoveringMetadata();
        }

        public void RecoveringMetadata()
        {
            Receive<ReplicationRead>(r => r.FromSequenceNr > (link.LocalClock.SequenceNr + 1L), r =>
            {
                log.Debug("[Recovery of {0}] Trigger update of inconsistent replication progress at {1}", endpointId, link.RemoteLogId);
                Sender.Tell(new ReplicationReadSuccess(Enumerable.Empty<DurableEvent>(), link.LocalClock.SequenceNr, link.RemoteLogId, link.LocalClock.VersionVector));
            });
            Receive<ReplicationRead>(r =>
            {
                Sender.Tell(new ReplicationReadSuccess(Enumerable.Empty<DurableEvent>(), r.FromSequenceNr - 1, link.RemoteLogId, link.LocalClock.VersionVector));
                Context.Parent.Tell(new Acceptor.RecoveryStepCompleted(link));
                Become(RecoveringEvents);
            });
        }

        public void RecoveringEvents()
        {
            Receive<ReplicationWriteSuccess>(success =>
            {
                if(link.RemoteSequenceNr <= success.StoredReplicationProgress)
                    Context.Parent.Tell(new Acceptor.RecoveryStepCompleted(link));
            });
        }
    }
}