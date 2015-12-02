using System;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Eventsourced.ReplicationProtocol;

namespace Akka.Eventsourced
{

    [Serializable]
    internal struct ReplicationSource
    {
        public readonly string EndpointId;
        public readonly string LogName;
        public readonly string LogId;
        public readonly ActorSelection Acceptor;

        public ReplicationSource(string endpointId, string logName, string logId, ActorSelection acceptor) : this()
        {
            EndpointId = endpointId;
            LogName = logName;
            LogId = logId;
            Acceptor = acceptor;
        }
    }

    [Serializable]
    public struct ReplicationTarget
    {
        public readonly ReplicationEndpoint Endpoint;
        public readonly string LogName;
        public readonly string LogId;
        public readonly IActorRef Log;

        public ReplicationTarget(ReplicationEndpoint endpoint, string logName, string logId, IActorRef log) : this()
        {
            Endpoint = endpoint;
            LogName = logName;
            LogId = logId;
            Log = log;
        }
    }

    [Serializable]
    internal struct ReplicationLink
    {
        public readonly ReplicationSource Source;
        public readonly ReplicationTarget Target;

        public ReplicationLink(ReplicationSource source, ReplicationTarget target) : this()
        {
            Source = source;
            Target = target;
        }
    }

    internal class SourceConnector
    {
        private ReplicationEndpoint _endpoint;
        private ReplicationConnection _connection;

        public SourceConnector(ReplicationEndpoint endpoint, ReplicationConnection connection)
        {
            _endpoint = endpoint;
            _connection = connection;
        }

        public ActorSelection RemoteAcceptor
        {
            get { return RemoteActorSelection(Acceptor.Name); }
        }

        private ActorSelection RemoteActorSelection(string actor)
        {
            var protocol = "akka.tcp";
            if (_endpoint.System is ExtendedActorSystem)
                protocol = ((ExtendedActorSystem) _endpoint.System).Provider.DefaultAddress.Protocol;

            return _endpoint.System.ActorSelection(string.Format("{0}://{1}@{2}:{3}/user/{4}", protocol, _connection.Name, _connection.Host, _connection.Port, actor));
        }

        public IImmutableSet<ReplicationLink> GetLinks(ReplicationEndpointInfo sourceInfo)
        {
            return _endpoint.CommonLogNames(sourceInfo)
                .Select(logName =>
                {
                    var sourceLogId = sourceInfo.LogId(logName);
                    var source = new ReplicationSource(sourceInfo.EndpointId, logName, sourceLogId, RemoteAcceptor);
                    return new ReplicationLink(source, _endpoint.Target(logName));
                }).ToImmutableHashSet();
        } 
        
        public void Activate()
        {
            _endpoint.System.ActorOf(Props.Create(() => new Connector(this)));
        }
    }

    internal class Connector : ReceiveActor
    {
        public Connector(SourceConnector sourceConnector)
        {
            throw new NotImplementedException();
        }
    }

    internal class Replicator
    {

    }

    internal class FailureDetector : UntypedActor
    {
        protected override void OnReceive(object message)
        {
            throw new NotImplementedException();
        }
    }
}