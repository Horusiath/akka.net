using Akka.Actor;

namespace Akka.Eventsourced
{
    public struct DeliveryAttempt
    {
        public readonly string DeliveryId;
        public readonly object Message;
        public readonly ActorPath Destination;

        public DeliveryAttempt(string deliveryId, object message, ActorPath destination)
        {
            DeliveryId = deliveryId;
            Message = message;
            Destination = destination;
        }
    }

    public class ConfirmedDeliverySemantic
    {
    }
}