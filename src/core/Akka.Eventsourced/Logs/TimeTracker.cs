using System;

namespace Akka.Eventsourced.Logs
{
    [Serializable]
    public struct TimeTracker
    {
        public static readonly TimeTracker Empty = new TimeTracker(0L, 0L, VectorTime.Zero);

        public readonly long UpdateCount;
        public readonly long SequenceNr;
        public readonly VectorTime VectorTime;

        public TimeTracker(long updateCount, long sequenceNr, VectorTime vectorTime)
        {
            UpdateCount = updateCount;
            SequenceNr = sequenceNr;
            VectorTime = vectorTime;
        }
    }
}