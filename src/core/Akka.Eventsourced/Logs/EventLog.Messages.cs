using System;

namespace Akka.Eventsourced.Logs
{
    public partial class EventLog
    {
        [Serializable]
        internal struct RecoveredState
        {
            public readonly EventLogClock Clock;
            public readonly DeletionMetadata DeleteMetadata;

            public RecoveredState(EventLogClock clock, DeletionMetadata deleteMetadata)
            {
                Clock = clock;
                DeleteMetadata = deleteMetadata;
            }
        }

        [Serializable]
        internal struct RecoverySuccess
        {
            public readonly RecoveredState State;

            public RecoverySuccess(RecoveredState state)
            {
                State = state;
            }
        }

        [Serializable]
        internal struct RecoveryFailure
        {
            public readonly Exception Cause;

            public RecoveryFailure(Exception cause)
            {
                Cause = cause;
            }
        }

        [Serializable]
        internal sealed class PhysicalDelete
        {
            public static readonly PhysicalDelete Instance = new PhysicalDelete();

            private PhysicalDelete()
            {
            }
        }

        [Serializable]
        internal struct PhysicalDeleteSuccess
        {
            public readonly long DeletedTo;

            public PhysicalDeleteSuccess(long deletedTo)
            {
                DeletedTo = deletedTo;
            }
        }

        [Serializable]
        internal struct PhysicalDeleteFailure
        {
            public readonly Exception Cause;

            public PhysicalDeleteFailure(Exception cause)
            {
                Cause = cause;
            }
        }
    }
}