using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Configuration;
using Akka.Eventsourced.EventsourcedProtocol;
using Akka.Eventsourced.ReplicationProtocol;

namespace Akka.Eventsourced.Logs
{
    [Serializable]
    public sealed class BatchingSettings
    {
        public static BatchingSettings Create(ActorSystem system)
        {
            return Create(system.Settings.Config.GetConfig("akka.eventsourced.log.batching"));
        }

        public static BatchingSettings Create(Config config)
        {
            return new BatchingSettings(
                batchSizeLimit: config.GetInt("batch-size-limit"));
        }

        public readonly int BatchSizeLimit;

        public BatchingSettings(int batchSizeLimit)
        {
            BatchSizeLimit = batchSizeLimit;
        }
    }

    /// <summary>
    /// Wrapper over event log used for batching <see cref="Write"/> commands into <see cref="WriteMany"/> messages.
    /// <see cref="ReplicationWrite"/>s are batched into <see cref="ReplicationWriteMany"/> message.
    /// </summary>
    public class BatchingLayer : ReceiveActor
    {
        public BatchingLayer(Props eventLogProps)
        {
            var eventLog = Context.ActorOf(eventLogProps);
            var defaultBatcher = Context.ActorOf(Props.Create(() => new DefaultBatcher(eventLog)), "default-batcher");
            var replicationBatcher = Context.ActorOf(Props.Create(() => new ReplicationBatcher(eventLog)), "replication-batcher");

            Receive<ReplicationWrite>(write => replicationBatcher.Forward(write.WithInitiator(Sender)));
            Receive<ReplicationRead>(read => replicationBatcher.Forward(read));
            ReceiveAny(message => defaultBatcher.Forward(message));
        }
    }

    internal abstract class Batcher<TEventBatch> : ActorBase
        where TEventBatch : IDurableEventBatch
    {
        protected readonly BatchingSettings Settings = BatchingSettings.Create(Context.System);
        protected ImmutableArray<TEventBatch> Batch;

        public Batcher()
        {
            Batch = ImmutableArray<TEventBatch>.Empty;
        }

        protected abstract IActorRef EventLog { get; }
        protected abstract object WriteRequest(IEnumerable<TEventBatch> batches);
        protected abstract bool Idle(object message);

        protected override bool Receive(object message)
        {
            return Idle(message);
        }

        protected override void Unhandled(object message)
        {
            EventLog.Forward(message);
        }

        protected void WriteAll()
        {
            while (TryWriteBatch()) ;
        }

        protected bool TryWriteBatch()
        {
            if (Batch.IsEmpty) return false;

            int num = 0, i = 0;
            var writes = new List<TEventBatch>();
            for (; i < Batch.Length; i++)
            {
                var write = Batch[i];
                var count = write.Count;
                num += count;
                if (num <= Settings.BatchSizeLimit || num == count)
                {
                    writes.Add(write);
                }
            }

            EventLog.Tell(WriteRequest(writes));

            Batch = Batch.RemoveRange(0, i);
            return !Batch.IsEmpty;
        }
    }

    internal class DefaultBatcher : Batcher<Write>
    {
        private readonly IActorRef eventLog;

        public DefaultBatcher(IActorRef eventLog)
        {
            this.eventLog = eventLog;
        }

        protected override IActorRef EventLog { get { return eventLog; } }

        protected override object WriteRequest(IEnumerable<Write> batches)
        {
            return new WriteMany(batches);
        }

        protected override bool Idle(object message)
        {
            if (message is Write)
            {
                Batch = Batch.Add((Write) message);
                TryWriteBatch();
                Context.Become(Writing);

                return true;
            }

            return false;
        }

        protected bool Writing(object message)
        {
            return message.Match()
                .With<Write>(write => Batch = Batch.Add(write))
                .With<WriteManyComplete>(_ =>
                {
                    if (Batch.IsEmpty) Context.Become(Idle);
                    else TryWriteBatch();
                })
                .With<Replay>(replay =>
                {
                    WriteAll();
                    EventLog.Forward(replay);
                    Context.Become(Idle);
                })
                .WasHandled;
        }
    }

    internal class ReplicationBatcher : Batcher<ReplicationWrite>
    {
        private readonly IActorRef eventLog;

        public ReplicationBatcher(IActorRef eventLog)
        {
            this.eventLog = eventLog;
        }

        protected override IActorRef EventLog { get { return eventLog; } }
        protected override object WriteRequest(IEnumerable<ReplicationWrite> batches)
        {
            return new ReplicationWriteMany(batches);
        }

        protected override bool Idle(object message)
        {
            if (message is ReplicationWrite)
            {
                Batch = Batch.Add((ReplicationWrite) message);
                TryWriteBatch();
                Context.Become(Writing);

                return true;
            }
            return false;
        }

        private bool Writing(object message)
        {
            return message.Match()
                .With<ReplicationWrite>(write => Batch = Batch.Add(write.WithInitiator(Sender)))
                .With<ReplicationWriteManyComplete>(_ =>
                {
                    if (Batch.IsEmpty) Context.Become(Idle);
                    else TryWriteBatch();
                })
                .WasHandled;
        }
    }
}