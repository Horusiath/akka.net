using System;
using Akka.Actor;

namespace Akka.Eventsourced.Logs
{
    public class BatchingEventLog : UntypedActor
    {
        protected override void OnReceive(object message)
        {
            throw new NotImplementedException();
        }
    }

    [Serializable]
    public sealed class BatchingSettings
    {
        
    }
}