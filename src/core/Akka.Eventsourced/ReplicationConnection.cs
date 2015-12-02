using System.Collections.Immutable;

namespace Akka.Eventsourced
{
    public struct ReplicationConnection
    {
        public const string DefaultRemoteSystemName = "location";

        public readonly string Host;
        public readonly int Port;
        public readonly string Name;
        public readonly IImmutableDictionary<string, IReplicationFilter> Filters;

        public ReplicationConnection(string host, int port, string name = null, IImmutableDictionary<string, IReplicationFilter> filters = null)
        {
            Host = host;
            Port = port;
            Name = name ?? DefaultRemoteSystemName;
            Filters = filters ?? ImmutableDictionary<string, IReplicationFilter>.Empty;
        }
    }
}