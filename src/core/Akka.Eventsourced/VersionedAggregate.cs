using System;

namespace Akka.Eventsourced
{
    public sealed class VersionedAggregate<TState, TCommand, TEvent>
    {
        public readonly string Id;
        public readonly Func<TState, TCommand, TEvent> CommandHandler;
        public readonly Func<TState, TEvent, TState> EventHandler;
        public readonly IConcurrentVersions<TState, TEvent> Aggregate;

        public VersionedAggregate(string id, Func<TState, TCommand, TEvent> commandHandler, Func<TState, TEvent, TState> eventHandler, 
            IConcurrentVersions<TState, TEvent> aggregate = null)
        {
            Id = id;
            CommandHandler = commandHandler;
            EventHandler = eventHandler;
            Aggregate = aggregate;
        }
    }
}