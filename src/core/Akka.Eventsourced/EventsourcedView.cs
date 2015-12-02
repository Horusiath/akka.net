using System;
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Eventsourced.EventsourcedProtocol;
using Akka.Pattern;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Eventsourced
{
    public delegate void Handler<T>(Result<T> result);

    public abstract class EventsourcedView : ActorBase, IWithUnboundedStash, IConditionalCommands
    {
        private readonly AtomicCounterLong instanceIdCounter = new AtomicCounterLong(1);

        public readonly long InstanceId;
        public readonly ReplaySettings ReplaySettings;

        private readonly IStash messageStash;

        private bool isRecovering = true;
        private DurableEvent lastEventHandled;
        private VectorClock clock;
        private IImmutableDictionary<SnapshotMetadata, Handler<SnapshotMetadata>> saveRequests =
            ImmutableDictionary<SnapshotMetadata, Handler<SnapshotMetadata>>.Empty;

        protected EventsourcedView()
        {
            messageStash = Context.CreateStash(this);
            InstanceId = instanceIdCounter.GetAndIncrement();
            ReplaySettings = ReplaySettings.Create(Context.System);
        }

        public IStash Stash { get; set; }

        private ILoggingAdapter log;
        public ILoggingAdapter Log { get { return log ?? (log = Context.GetLogger()); } }

        private IActorRef commandManager;
        public IActorRef CommandManager
        {
            get
            {
                return commandManager ?? (commandManager = Context.ActorOf(Props.Create(() => new CommandManager(Self))));
            }
        }

        public bool IsRecovering { get { return isRecovering; } }
        public DurableEvent LastHandledEvent { get { return lastEventHandled; } }
        public VectorTime CurrentTime { get { return clock.CurrentTime; } }

        public virtual string AggregateId { get { return null; } }
        public virtual bool HasSharedClockEntry { get { return true; } }
        public virtual int MaxChunkSize { get { return ReplaySettings.MaxChunkSize; } }

        public abstract string Id { get; }
        public abstract IActorRef EventLog { get; }
        public abstract bool OnCommand(object message);
        public abstract bool OnEvent(object message);
        public virtual bool OnSnapshot(object message)
        {
            return true;
        }

        public virtual void OnRecovered() { }

        protected void Recovered()
        {
            isRecovering = false;
            OnRecovered();
        }

        protected void OnEventInternal(DurableEvent e, Func<bool> handler = null)
        {
            var initialClock = clock;
            var initialEvent = lastEventHandled;

            lastEventHandled = e;
            if (HasSharedClockEntry)
            {
                clock = clock.Set(e.LocalLogId, e.LocalSequenceNr);
                if (e.EmitterId != Id) clock = clock.Merge(e.VectorTimestamp);
            }
            else
            {
                if (e.EmitterId != Id) clock = clock.Update(e.VectorTimestamp);
                else if (isRecovering) clock = clock.Merge(e.VectorTimestamp);
            }

            if (handler != null && !handler())
            {
                clock = initialClock;
                lastEventHandled = initialEvent;
            }
        }

        protected void OnEventInternal(DurableEvent e, Exception cause)
        {
            lastEventHandled = e;
        }

        protected VectorTime IncrementLocalTime()
        {
            clock = clock.Tick();
            return clock.CurrentTime;
        }

        public void Save(object snapshot, Handler<SnapshotMetadata> handler)
        {
            //TODO
            //val payload = snapshot match {
            //  case tree: ConcurrentVersionsTree[_, _] => tree.copy()
            //  case other => other
            //}

            var payload = snapshot;
            var prototype = new Snapshot(payload, Id, lastEventHandled, CurrentTime);
            var metadata = prototype.Metadata;

            if (saveRequests.ContainsKey(metadata))
            {
                var err = new IllegalStateException(string.Format("Snapshot with metadata {0} is currently being saved", metadata));
                handler(Result.Failure<SnapshotMetadata>(err));
            }
            else
            {
                saveRequests = saveRequests.Add(metadata, handler);
                var s = CapturedSnapshot(prototype);
                EventLog.Tell(new SaveSnapshot(s, Sender, Self, InstanceId));
            }
        }

        protected virtual Snapshot CapturedSnapshot(Snapshot snapshot)
        {
            return snapshot;
        }

        protected void LoadedSnapshot(Snapshot snapshot)
        {
            lastEventHandled = snapshot.LastEvent;
            clock = new VectorClock(clock.ProcessId, snapshot.CurrentTime);
        }

        protected virtual void UnhandedMessage(object message)
        {
            OnCommand(message);
        }

        private void Load()
        {
            EventLog.Tell(new LoadSnapshot(Id, Self, InstanceId));
        }

        private void Replay(long fromSequenceNr = 1L)
        {
            EventLog.Tell(new Replay(fromSequenceNr, MaxChunkSize, Self, AggregateId, InstanceId));
        }

        protected sealed override bool Receive(object message)
        {
            return Initializing(message);
        }

        private bool Initializing(object message)
        {
            return message.Match()
                .With<LoadSnapshotSuccess>(success =>
                {
                    if (success.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        var snapshot = success.Snapshot;
                        if (snapshot == null) Replay();
                        else
                        {
                            LoadedSnapshot(snapshot);
                            if (!OnSnapshot(snapshot))
                            {
                                Zeroize();
                                Log.Warning("Snapshot loaded (metadata = {0}) but not handled, replaying from scratch.", snapshot.Metadata);
                                Replay(snapshot.Metadata.SequenceNr + 1);
                            }
                            else Replay();
                        }
                    }
                })
                .With<LoadSnapshotFailure>(failure =>
                {
                    if (failure.InstanceId != InstanceId) Unhandled(message);
                    else Replay();
                })
                .With<Replaying>(replaying =>
                {
                    if (replaying.InstanceId != InstanceId) Unhandled(message);
                    else ReceiveEvent(replaying.Event);
                })
                .With<ReplaySuspended>(suspended =>
                {
                    if (suspended.InstanceId != InstanceId) Unhandled(message);
                    else Sender.Tell(new ReplayNext(MaxChunkSize, suspended.InstanceId));
                })
                .With<ReplaySuccess>(success =>
                {
                    if (success.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        Context.Become(Initialized);
                        ConditionChanged(lastEventHandled.VectorTimestamp);
                        Stash.UnstashAll();
                        Recovered();
                    }
                })
                .With<ReplayFailure>(failure =>
                {
                    if (failure.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        Log.Error(failure.Cause, "Replay failed, stopping self");
                        Context.Stop(Self);
                    }
                })
                .Default(_ => Stash.Stash())
                .WasHandled;
        }

        private bool Initialized(object message)
        {
            return message.Match()
                .With<Written>(written =>
                {
                    if (written.Event.LocalSequenceNr > lastEventHandled.LocalSequenceNr) Unhandled(message);
                    else ReceiveEvent(written.Event);
                })
                .With<ConditionalCommand>(conditionalCommand => ConditionalSend(conditionalCommand.Condition, conditionalCommand.Command))
                .With<SaveSnapshotSuccess>(success =>
                {
                    if (success.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        var metadata = success.Metadata;
                        Handler<SnapshotMetadata> handler;
                        if (saveRequests.TryGetValue(metadata, out handler))
                        {
                            handler(Result.Success(metadata));
                            saveRequests = saveRequests.Remove(metadata);
                        }
                    }
                })
                .With<SaveSnapshotFailure>(failure =>
                {
                    if (failure.InstanceId != InstanceId) Unhandled(message);
                    else
                    {
                        var metadata = failure.Metadata;
                        Handler<SnapshotMetadata> handler;
                        if (saveRequests.TryGetValue(metadata, out handler))
                        {
                            handler(Result.Success(metadata));
                            saveRequests = saveRequests.Remove(metadata);
                        }
                    }
                })
                .Default(_ => UnhandedMessage(message))
                .WasHandled;
        }

        private void ReceiveEvent(DurableEvent e)
        {
            OnEventInternal(e, () => OnEvent(e.Payload));

            if (!IsRecovering)
            {
                ConditionChanged(e.VectorTimestamp);
            }
        }

        protected void ConditionChanged(VectorTime time)
        {
            CommandManager.Tell(time);
        }

        protected void ConditionalSend(VectorTime condition, object command)
        {
            CommandManager.Tell(new Command(condition, command, Sender));
        }

        protected override void PostStop()
        {
            Stash.UnstashAll();
            base.PostStop();
        }

        protected override void PreStart()
        {
            Zeroize();
            Load();
        }

        private void Zeroize()
        {
            lastEventHandled = new DurableEvent(null, Id);
            clock = new VectorClock(Id, VectorTime.Zero);
        }

        protected override void PreRestart(Exception reason, object message)
        {
            Stash.UnstashAll();
            base.PreRestart(reason, message);
        }
    }

    [Serializable]
    public sealed class ReplaySettings
    {
        public static ReplaySettings Create(ActorSystem system)
        {
            return Create(system.Settings.Config.GetConfig("akka.eventsourced.replay"));
        }

        public static ReplaySettings Create(Config config)
        {
            return new ReplaySettings(
                maxChunkSize: config.GetInt("max-chunk-size"));
        }

        public readonly int MaxChunkSize;

        public ReplaySettings(int maxChunkSize)
        {
            MaxChunkSize = maxChunkSize;
        }
    }
}