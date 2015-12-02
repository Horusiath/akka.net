using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Eventsourced.ReplicationProtocol;
using Akka.Pattern;
using Akka.Util;

namespace Akka.Eventsourced
{
    [Serializable]
    public sealed class ReplicationSettings
    {
        public static ReplicationSettings Create(ActorSystem system)
        {
            return Create(system.Settings.Config.GetConfig("akka.eventsourced.replication.log"));
        }

        public static ReplicationSettings Create(Config config)
        {
            return new ReplicationSettings(
                retryInterval: config.GetTimeSpan("retry-interval"),
                readTimeout: config.GetTimeSpan("read-timeout"),
                writeTimeout: config.GetTimeSpan("write-timeout"),
                maxBatchSize: config.GetInt("max-batch-size"),
                failureDetectionLimit: config.GetTimeSpan("failure-detection-limit"));
        }

        public readonly TimeSpan RetryInterval;
        public readonly TimeSpan ReadTimeout;
        public readonly TimeSpan WriteTimeout;
        public readonly int MaxBatchSize;
        public readonly TimeSpan FailureDetectionLimit;

        public ReplicationSettings(TimeSpan retryInterval, TimeSpan readTimeout, TimeSpan writeTimeout, int maxBatchSize, TimeSpan failureDetectionLimit)
        {
            RetryInterval = retryInterval;
            ReadTimeout = readTimeout;
            WriteTimeout = writeTimeout;
            MaxBatchSize = maxBatchSize;
            FailureDetectionLimit = failureDetectionLimit;
        }
    }

    public class ReplicationEndpoint
    {
        #region messages

        [Serializable]
        public struct Available
        {
            public readonly string EndpointId;
            public readonly string LogName;

            public Available(string endpointId, string logName) : this()
            {
                EndpointId = endpointId;
                LogName = logName;
            }
        }

        [Serializable]
        public struct Unavailable
        {
            public readonly string EndpointId;
            public readonly string LogName;

            public Unavailable(string endpointId, string logName)
            {
                EndpointId = endpointId;
                LogName = logName;
            }
        }
        
        #endregion

        public const string DefaultLogName = "default";
        private static Tuple<string, int> ToAddress(string address)
        {
            var splits = address.Split(';');
            return Tuple.Create(splits[0], int.Parse(splits[1]));
        }

        public static ReplicationEndpoint Create(Func<string, Props> logFactory, ActorSystem system)
        {
            var config = system.Settings.Config;
            var connections = config.GetStringList("akka.eventsourced.endpoint.connections")
                .Select(ToAddress)
                .Select(pair => new ReplicationConnection(pair.Item1, pair.Item2))
                .ToImmutableHashSet();

            return Create(logFactory, connections, system);
        }

        private static ReplicationEndpoint Create(Func<string, Props> logFactory, ImmutableHashSet<ReplicationConnection> connections, ActorSystem system)
        {
            var endpointId = system.Settings.Config.GetString("akka.eventsourced.endpoint.id");
            return new ReplicationEndpoint(endpointId, ImmutableHashSet.Create(DefaultLogName), logFactory, connections, system);
        }

        public readonly string Id;
        public readonly IImmutableSet<string> LogNames;
        public readonly Func<string, Props> LogFactory;
        public readonly IImmutableSet<ReplicationConnection> Connections;
        public readonly ActorSystem System;

        public readonly ReplicationSettings Settings;
        public readonly ReplicationEndpointInfo Info;
        public readonly IImmutableDictionary<string, IActorRef> Logs;

        private readonly AtomicReference<bool> _isActive = new AtomicReference<bool>(false);
        private readonly IActorRef _acceptor;
        private readonly IImmutableSet<SourceConnector> _connectors;

        public ReplicationEndpoint(string id, IImmutableSet<string> logNames, Func<string, Props> logFactory, IImmutableSet<ReplicationConnection> connections, ActorSystem system)
        {
            Id = id;
            LogNames = logNames;
            LogFactory = logFactory;
            Connections = connections;
            System = system;

            Settings = ReplicationSettings.Create(system);
            Info = new ReplicationEndpointInfo(id, logNames);
            Logs = logNames.ToImmutableDictionary(x => x, x => system.ActorOf(logFactory(x)));

            _acceptor = system.ActorOf(Props.Create(() => new Acceptor(this)), Acceptor.Name);
            _connectors = connections.Select(x => new SourceConnector(this, x)).ToImmutableHashSet();
        }

        public string LogId(string logName)
        {
            return Info.LogId(logName);
        }

        public async Task Recover()
        {
            if (_isActive.CompareAndSet(false, true))
            {
                var promise = new TaskCompletionSource<object>();
                var recovery = new Recovery(this);
                var infos = await recovery.ReadEndpointInfoAsync();
                var trackers = await recovery.ReadTimeTrackersAsync();

                var phase1 = Task.Run<IEnumerable<RecoveryLink>>(async () =>
                {
                    var deleteSnapshots = new List<Task>();
                    var links = new List<RecoveryLink>();
                    foreach (var link in recovery.GetRecoveryLinks(infos, trackers))
                    {
                        deleteSnapshots.Add(recovery.DeleteSnapshotsAsync(link));
                        links.Add(link);
                    }

                    await Task.WhenAll(deleteSnapshots);
                    return links;
                });

                var phase2 = Task.Run(async () =>
                {
                    var _ = await phase1;
                    var r = await promise.Task;
                    _isActive.Value = false;
                    return r;
                });

                phase1.ContinueWith(t => _acceptor.Tell(new Acceptor.Recover(t.Result.ToImmutableHashSet(), promise)));
                await phase2.ConfigureAwait(false);
            }
            else throw new IllegalStateException("Recovery running or endpoint already activated");
        }

        public void Activate()
        {
            if (_isActive.CompareAndSet(false, true))
            {
                _acceptor.Tell(Acceptor.Process.Instance);
                foreach (var connector in _connectors)
                    connector.Activate();
            }
            else throw new IllegalStateException("Recovery running or endpoint already activated");
        }

        internal IImmutableSet<string> CommonLogNames(ReplicationEndpointInfo info)
        {
            return LogNames.Intersect(info.LogNames);
        }

        public ReplicationTarget Target(string logName)
        {
            return new ReplicationTarget(this, logName, LogId(logName), Logs[logName]);
        }
    }
}