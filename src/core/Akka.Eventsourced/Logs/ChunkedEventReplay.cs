using System;
using System.Collections.Generic;
using Akka.Actor;
using Akka.Eventsourced.EventsourcedProtocol;

namespace Akka.Eventsourced.Logs
{
    internal class ChunkedEventReplay : ReceiveActor
    {
        public readonly IEnumerator<DurableEvent> enumerator;
        public readonly IActorRef requestor;

        public ChunkedEventReplay(IActorRef requestor, Func<IEnumerator<DurableEvent>> enumeratorFactory)
        {
            this.requestor = requestor;
            this.enumerator = enumeratorFactory();

            Receive<ReplayNext>(next =>
            {
                try
                {
                    var n = next.Max;
                    var hasNext = true;
                    while (n > 0 && (hasNext = enumerator.MoveNext()))
                    {
                        n--;
                        requestor.Tell(new Replaying(enumerator.Current, next.InstanceId));
                    }

                    if (!hasNext)
                    {
                        requestor.Tell(new ReplaySuccess(next.InstanceId));
                        Context.Stop(Self);
                    }
                    else requestor.Tell(new ReplaySuspended(next.InstanceId));
                }
                catch (Exception cause)
                {
                    requestor.Tell(new ReplayFailure(cause, next.InstanceId));
                    Context.Stop(Self);
                }
            });
            Receive<Terminated>(terminated => Equals(terminated.ActorRef, requestor), _ => Context.Stop(Self));
        }

        protected override void PreStart()
        {
            Context.Watch(requestor);
        }

        protected override void PostStop()
        {
            enumerator.Dispose();
        }
    }
}