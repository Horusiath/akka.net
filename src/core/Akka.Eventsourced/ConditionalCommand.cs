using System;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;

namespace Akka.Eventsourced
{
    public interface IConditionalCommands
    {
        IActorRef CommandManager { get; }
    }

    [Serializable]
    public struct ConditionalCommand
    {
        public readonly VectorTime Condition;
        public readonly object Command;

        public ConditionalCommand(VectorTime condition, object command)
        {
            Condition = condition;
            Command = command;
        }
    }

    [Serializable]
    internal struct Command
    {
        public readonly VectorTime Condition;
        public readonly object Payload;
        public readonly IActorRef Sender;

        public Command(VectorTime condition, object payload, IActorRef sender)
        {
            Condition = condition;
            Payload = payload;
            Sender = sender;
        }
    }

    [Serializable]
    internal struct Send
    {
        public readonly VectorTime OlderThan;

        public Send(VectorTime olderThan)
        {
            OlderThan = olderThan;
        }
    }

    [Serializable]
    internal struct Sent
    {
        public readonly VectorTime OlderThan;
        public readonly int Num;

        public Sent(VectorTime olderThan, int num)
        {
            OlderThan = olderThan;
            Num = num;
        }
    }

    internal sealed class CommandBuffer : ReceiveActor
    {
        private readonly IActorRef owner;
        private ImmutableArray<Command> commands = ImmutableArray<Command>.Empty;

        public CommandBuffer(IActorRef owner)
        {
            this.owner = owner;

            Receive<Send>(send => Sender.Tell(new Sent(send.OlderThan, Send(send.OlderThan))));
            Receive<Command>(command => commands = commands.Add(command));
        }

        private int Send(VectorTime olderThan)
        {
            var splits = commands
                .GroupBy(x => x.Condition <= olderThan)
                .ToDictionary(x => x.Key, ImmutableArray.CreateRange);

            if (!splits.TryGetValue(false, out commands))
                commands = ImmutableArray<Command>.Empty;

            ImmutableArray<Command> older;
            if (splits.TryGetValue(true, out older))
                foreach (var command in older)
                    owner.Tell(command.Payload, command.Sender);

            return older.Length;
        }
    }

    internal sealed class CommandManager : ReceiveActor
    {
        private readonly IActorRef owner;
        private readonly IActorRef commandBuffer;
        private VectorTime currentVersion = VectorTime.Zero;

        public CommandManager(IActorRef owner)
        {
            this.owner = owner;
            this.commandBuffer = Context.ActorOf(Props.Create(() => new CommandBuffer(owner)));

            Idle();
        }

        private void Idle()
        {
            Receive<Command>((Action<Command>)Process);
            Receive<VectorTime>(time =>
            {
                currentVersion = currentVersion.Merge(time);
                commandBuffer.Tell(new Send(currentVersion));
                Become(Sending);
            });
        }

        private void Sending()
        {
            Receive<Command>((Action<Command>)Process);
            Receive<VectorTime>(time => currentVersion = currentVersion.Merge(time));
            Receive<Sent>(sent => sent.OlderThan == currentVersion, _ => Become(Idle));
            Receive<Sent>(sent => commandBuffer.Tell(new Send(currentVersion)));
        }

        private void Process(Command command)
        {
            if (command.Condition <= currentVersion) owner.Tell(command.Payload, command.Sender);
            else commandBuffer.Tell(command);
        }
    }
}