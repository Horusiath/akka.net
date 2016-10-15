//-----------------------------------------------------------------------
// <copyright file="PerformanceActors.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Persistence;
using Akka.Actor;

namespace PersistenceBenchmark
{
    sealed class EventsourcedActor : ReceivePersistentActor
    {
        private int state = 0;

        public EventsourcedActor(string persistenceId)
        {
            PersistenceId = persistenceId;

            Recover<Evt>(e => state += e.Value);
            Command<Cmd>(cmd => Persist(new Evt(cmd.Value), e => state += e.Value));
            Command<InitMsg>(_ => Sender.Tell(Initialized.Instance));
            Command<Complete>(_ => Sender.Tell(new Completed(state)));
        }

        public override string PersistenceId { get; }
    }

    sealed class CommandsourcedActor : ReceivePersistentActor
    {
        private int state = 0;

        public CommandsourcedActor(string persistenceId)
        {
            PersistenceId = persistenceId;

            Recover<Evt>(e => state += e.Value);
            Command<Cmd>(cmd => PersistAsync(new Evt(cmd.Value), e => state += e.Value));
            Command<InitMsg>(_ => Sender.Tell(Initialized.Instance));
            Command<Complete>(_ => DeferAsync(new Evt(1), e => Sender.Tell(new Completed(state))));
        }

        public override string PersistenceId { get; }
    }
    public sealed class Complete
    {
        public static readonly Complete Instance = new Complete();
        private Complete() { }
    }

    public sealed class Completed
    {
        public readonly int Value;

        public Completed(int value)
        {
            Value = value;
        }
    }

    public sealed class InitMsg
    {
        public static readonly InitMsg Instance = new InitMsg();
        private InitMsg() { }
    }

    public sealed class Initialized
    {
        public static readonly Initialized Instance = new Initialized();
        private Initialized() { }
    }

    public sealed class Cmd
    {
        public readonly int Value;

        public Cmd(int value)
        {
            Value = value;
        }
    }

    public sealed class Evt
    {
        public readonly int Value;

        public Evt(int value)
        {
            Value = value;
        }
    }
}