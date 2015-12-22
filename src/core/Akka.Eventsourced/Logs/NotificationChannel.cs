using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Eventsourced.ReplicationProtocol;

namespace Akka.Eventsourced.Logs
{
    /// <summary>
    /// Notifies registered <see cref="Replicator"/>s about source log updates.
    /// </summary>
    internal class NotificationChannel : ReceiveActor
    {
        public NotificationChannel(string logId)
        {
            var registry = ImmutableDictionary<string, ReplicationRead>.Empty;
            var reading = ImmutableHashSet<string>.Empty;

            Receive<Updated>(updated =>
            {
                foreach (var entry in registry)
                {
                    var reg = entry.Value;
                    if (!reading.Contains(entry.Key) && updated.Events.Any(x => x.Replicable(reg.CurrentTargetVectorTime, reg.Filter)))
                    {
                        reg.Replicator.Tell(ReplicationDue.Instance);
                    }
                }
            });
            Receive<ReplicationRead>(read =>
            {
                registry = registry.Add(read.TargetLogId, read);
                reading = reading.Add(read.TargetLogId);
            });
            Receive<ReplicationReadSuccess>(success =>
            {
                reading = reading.Remove(success.TargetLogId);
            });
            Receive<ReplicationReadFailure>(failure =>
            {
                reading = reading.Remove(failure.TargetLogId);
            });
            Receive<ReplicationWrite>(write =>
            {
                ReplicationRead reg;
                if (registry.TryGetValue(write.SourceLogId, out reg))
                {
                    registry = registry.Add(write.SourceLogId, new ReplicationRead(
                        fromSequenceNr: reg.FromSequenceNr, 
                        maxNumberOfEvents: reg.MaxNumberOfEvents, 
                        filter: reg.Filter, 
                        targetLogId: reg.TargetLogId, 
                        replicator: reg.Replicator, 
                        currentTargetVectorTime: write.CurrentSourceVectorTime));
                }
            });
        }
    }

    [Serializable]
    internal struct Updated
    {
        public readonly IEnumerable<DurableEvent> Events;

        public Updated(IEnumerable<DurableEvent> events)
        {
            Events = events;
        }
    }
}