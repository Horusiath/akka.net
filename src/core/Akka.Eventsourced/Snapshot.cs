using System;
using System.Collections.Immutable;

namespace Akka.Eventsourced
{
    public struct SnapshotMetadata
    {
        public readonly string EmitterId;
        public readonly long SequenceNr;

        public SnapshotMetadata(string emitterId, long sequenceNr)
        {
            EmitterId = emitterId;
            SequenceNr = sequenceNr;
        }
    }

    [Serializable]
    public sealed class Snapshot
    {
        public readonly object Payload;
        public readonly string EmitterId;
        public readonly DurableEvent LastEvent;
        public readonly VectorTime CurrentTime;
        public readonly IImmutableList<DeliveryAttempt> DeliveryAttempts;

        public readonly SnapshotMetadata Metadata;

        public Snapshot(object payload, string emitterId, DurableEvent lastEvent, VectorTime currentTime, IImmutableList<DeliveryAttempt> deliveryAttempts = null)
        {
            Payload = payload;
            EmitterId = emitterId;
            LastEvent = lastEvent;
            CurrentTime = currentTime;
            DeliveryAttempts = deliveryAttempts ?? ImmutableList<DeliveryAttempt>.Empty;

            Metadata = new SnapshotMetadata(EmitterId, LastEvent.LocalSequenceNr);
        }

        public Snapshot Add(DeliveryAttempt deliveryAttempt)
        {
            return new Snapshot(
                payload: Payload,
                emitterId: EmitterId,
                lastEvent: LastEvent, 
                currentTime: CurrentTime,
                deliveryAttempts: DeliveryAttempts.Add(deliveryAttempt));
        }
    }
}