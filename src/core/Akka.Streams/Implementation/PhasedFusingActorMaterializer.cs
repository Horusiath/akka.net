#region copyright
//-----------------------------------------------------------------------
// <copyright file="PhasedFusingActorMaterializer.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------
#endregion

using Akka.Actor;
using Akka.Dispatch;
using Akka.Event;
using Akka.Pattern;
using Akka.Streams.Implementation.Fusing;
using Akka.Streams.Stage;
using Akka.Util;
using Akka.Util.Internal;
using Reactive.Streams;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Akka.Streams.Implementation
{
    public sealed class PhasedFusingActorMaterializer : ExtendedActorMaterializer
    {
        #region statics



        #endregion

        public override ActorMaterializerSettings Settings { get; }
        public override MessageDispatcher ExecutionContext { get; }
        public override ActorSystem System { get; }
        public override ILoggingAdapter Logger { get; }
        public override IActorRef Supervisor { get; }

        private readonly Dispatchers _dispatchers;
        private readonly AtomicBoolean _haveShutDown;
        private readonly SeqActorName _flowNames;
        private readonly Attributes _defaultAttributes;

        internal PhasedFusingActorMaterializer(ActorSystem system, ActorMaterializerSettings settings, Dispatchers dispatchers, IActorRef supervisor, AtomicBoolean haveShutDown, SeqActorName flowNames)
        {
            _dispatchers = dispatchers;
            _haveShutDown = haveShutDown;
            _flowNames = flowNames;

            Logger = Logging.GetLogger(system, this);
            Settings = settings;
            Supervisor = supervisor;
            System = system;

            _defaultAttributes = new Attributes(new Attributes.IAttribute[]
            {
                new Attributes.InputBuffer(settings.InitialInputBufferSize, settings.MaxInputBufferSize),
                new ActorAttributes.SupervisionStrategy(settings.SupervisionDecider),
                new ActorAttributes.Dispatcher(settings.Dispatcher == Deploy.NoDispatcherGiven ? Dispatchers.DefaultDispatcherId : settings.Dispatcher)
            });

            if (settings.IsFuzzingMode && !system.Settings.Config.HasPath("akka.stream.secret-test-fuzzing-warning-disable"))
            {
                Logger.Warning("Fuzzing mode is enabled on this system. If you see this warning on your production system then set akka.stream.materializer.debug.fuzzing-mode to off.");
            }

            if (!settings.IsAutoFusing)
            {
                Logger.Warning("Deprecated setting auto-fusing set to false. Since Akka 2.5.0 it does not have any effect and streams are always fused.");
            }
        }

        public override bool IsShutdown
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _haveShutDown.Value;
        }

        public override void Shutdown()
        {
            if (_haveShutDown.CompareAndSet(false, true))
                Supervisor.Tell(PoisonPill.Instance);
        }

        public override IMaterializer WithNamePrefix(string namePrefix) =>
            new PhasedFusingActorMaterializer(System, Settings, _dispatchers, Supervisor, _haveShutDown, _flowNames.Copy(namePrefix));

        public override ICancelable ScheduleRepeatedly(TimeSpan initialDelay, TimeSpan interval, Action action) =>
            System.Scheduler.Advanced.ScheduleRepeatedlyCancelable(initialDelay, interval, action);

        public override ActorMaterializerSettings EffectiveSettings(Attributes attributes)
        {
            throw new NotImplementedException();
        }

        public override ILoggingAdapter MakeLogger(object logSource) => Logging.GetLogger(System, logSource);

        public override TMat Materialize<TMat>(IGraph<ClosedShape, TMat> runnable) => Materialize(runnable, _defaultAttributes);

        public override TMat Materialize<TMat>(IGraph<ClosedShape, TMat> runnable, Attributes initialAttributes) =>
            Materialize(runnable, initialAttributes, DefaultPhase, DefaultPhases)

        public override ICancelable ScheduleOnce(TimeSpan delay, Action action)
        {
            throw new NotImplementedException();
        }

        public override TMat Materialize<TMat>(IGraph<ClosedShape, TMat> runnable, Func<GraphInterpreterShell, IActorRef> subFlowFuser)
        {
            throw new NotImplementedException();
        }

        public override TMat Materialize<TMat>(IGraph<ClosedShape, TMat> runnable, Func<GraphInterpreterShell, IActorRef> subFlowFuser, Attributes initialAttributes)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class SeqActorName
    {
        private readonly string _prefix;
        private readonly AtomicCounterLong _counter;

        public SeqActorName(string prefix, AtomicCounterLong counter)
        {
            _prefix = prefix;
            _counter = counter;
        }

        public string Next() => _prefix + "-" + _counter.GetAndIncrement();
        public SeqActorName Copy(string newPrefix) => new SeqActorName(newPrefix, _counter);
    }

    internal struct SegmentInfo
    {
        /// <summary>
        /// The island to which the segment belongs.
        /// </summary>
        public int GlobalIslandOffset { get; }

        /// <summary>
        /// How many slots are contained by the segment.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// The global slot where this segment starts.
        /// </summary>
        public int GlobalBaseOffset { get; }

        /// <summary>
        /// The local offset of the slot where this segment starts.
        /// </summary>
        public int RelativeBaseOffset { get; }

        public IPhaseIsland Phase { get; }

        public SegmentInfo(int globalIslandOffset, int length, int globalBaseOffset, int relativeBaseOffset, IPhaseIsland phase)
        {
            GlobalIslandOffset = globalIslandOffset;
            Length = length;
            GlobalBaseOffset = globalBaseOffset;
            RelativeBaseOffset = relativeBaseOffset;
            Phase = phase;
        }

        public override string ToString() => $"Segment(globalIslandOffset:{GlobalBaseOffset}, length:{Length}, globalBaseOffset:{GlobalBaseOffset}, relativeBaseOffset:{RelativeBaseOffset}, phase:{Phase})";
    }

    internal struct ForwardWire
    {
        public int IslandGlobalOffset { get; }
        public int ToGlobalOffset { get; }
        public OutPort From { get; }
        public object OutStage { get; }
        public IPhaseIsland Phase { get; }

        public ForwardWire(int islandGlobalOffset, int toGlobalOffset, OutPort from, object outStage, IPhaseIsland phase)
        {
            IslandGlobalOffset = islandGlobalOffset;
            ToGlobalOffset = toGlobalOffset;
            From = @from;
            OutStage = outStage;
            Phase = phase;
        }

        public override string ToString() => $"ForwardWire(islandGlobalOffset:{IslandGlobalOffset}, toGlobalOffset:{ToGlobalOffset}, phase:{Phase})";
    }

    internal struct SavedIslandData
    {
        public int IslandGlobalOffset { get; }
        public int LastVisitedOffset { get; }
        public int SkippedSlots { get; }
        public IPhaseIsland Phase { get; }

        public SavedIslandData(int islandGlobalOffset, int lastVisitedOffset, int skippedSlots, IPhaseIsland phase)
        {
            IslandGlobalOffset = islandGlobalOffset;
            LastVisitedOffset = lastVisitedOffset;
            SkippedSlots = skippedSlots;
            Phase = phase;
        }
    }

    internal class IslandTracking
    {
        public ImmutableDictionary<IslandTag, IPhase> Phases { get; }
        public ActorMaterializerSettings Settings { get; }
        public Attributes Attributes { get; }
        public IPhase DefaultPhase { get; }
        public PhasedFusingActorMaterializer Materializer { get; }
        public string IslandNamePrefix { get; }

        private int _islandNameCounter = 0;
        private int _currentGlobalOffset = 0;
        private int _currentSegmentGlobalOffset = 0;
        private int _currentIslandGlobalOffset = 0;
        // The number of slots that belong to segments of other islands encountered so far, from the
        // beginning of the island
        private int _currentIslandSkippedSlots = 0;

        private List<SegmentInfo> _segments;
        private List<IPhaseIsland> _activePhases;
        private List<ForwardWire> _forwardWires;
        private List<SavedIslandData> _islandStateStack;

        private IPhaseIsland _currentPhase;

        internal IPhaseIsland CurrentPhase
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _currentPhase;
        }

        internal int CurrentOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _currentGlobalOffset;
        }

        public IslandTracking(
            ImmutableDictionary<IslandTag, IPhase> phases,
            ActorMaterializerSettings settings,
            Attributes attributes,
            IPhase defaultPhase,
            PhasedFusingActorMaterializer materializer,
            string islandNamePrefix)
        {
            Phases = phases;
            Settings = settings;
            Attributes = attributes;
            DefaultPhase = defaultPhase;
            Materializer = materializer;
            IslandNamePrefix = islandNamePrefix;
        }

        private string NextIslandName() => IslandNamePrefix + (_islandNameCounter++);

        private void CompleteSegment()
        {
            var length = _currentGlobalOffset - _currentSegmentGlobalOffset;
            if (_activePhases is null)
            {
                _activePhases = new List<IPhaseIsland>(8);
                _islandStateStack = new List<SavedIslandData>(4);
            }

            if (length > 0)
            {
                // We just finished a segment by entering an island.
                var previousSegment = new SegmentInfo(
                    globalIslandOffset: _currentIslandGlobalOffset,
                    length: length,
                    globalBaseOffset: _currentSegmentGlobalOffset,
                    relativeBaseOffset: _currentSegmentGlobalOffset - _currentIslandGlobalOffset - _currentIslandSkippedSlots,
                    phase: _currentPhase);

                // Segment tracking is by demand, we only allocate this list if it is used.
                // If there are no islands, then there is no need to track segments
                if (_segments is null)
                    _segments = new List<SegmentInfo>(8);

                _segments.Add(previousSegment);
            }
        }

        internal void EnterIsland(IslandTag tag, Attributes attributes)
        {
            CompleteSegment();
            var previousPhase = _currentPhase;
            var previousIslandOffset = _currentIslandGlobalOffset;

            _islandStateStack.Add(new SavedIslandData(previousIslandOffset, _currentGlobalOffset, _currentIslandSkippedSlots, previousPhase));

            _currentPhase = Phases[tag].Apply(Settings, attributes, Materializer, NextIslandName());
            _activePhases.Add(_currentPhase);

            // Resolve the phase to be used to materialize this island
            _currentIslandGlobalOffset = _currentGlobalOffset;

            // The base offset of this segment is the current global offset
            _currentSegmentGlobalOffset = _currentGlobalOffset;
            _currentIslandSkippedSlots = 0;
        }

        internal void ExitIsland()
        {
            var lastStateIndex = _islandStateStack.Count - 1;
            var parentIsland = _islandStateStack[lastStateIndex]; //TODO: ref index?
            _islandStateStack.RemoveAt(lastStateIndex);
            CompleteSegment();

            // We start a new segment
            _currentSegmentGlobalOffset = _currentGlobalOffset;

            // We restore data for the island
            _currentIslandGlobalOffset = parentIsland.IslandGlobalOffset;
            _currentPhase = parentIsland.Phase;
            _currentIslandSkippedSlots = parentIsland.SkippedSlots + (_currentGlobalOffset - parentIsland.LastVisitedOffset);
        }

        internal void WireIn(InPort input, object logic)
        {
            // The slot for this InPort always belong to the current segment, so resolving its local
            // offset/slot is simple
            var localSlot = _currentGlobalOffset - _currentIslandGlobalOffset - _currentIslandSkippedSlots;

            // Assign the logic belonging to the current port to its calculated local slot in the island
            _currentPhase.AssignPort(input, localSlot, logic);

            // Check if there was any forward wiring that has this offset/slot as its target
            // First try to find such wiring
            var forwardWire = default(ForwardWire);
            var i = 0;
            if (!(_forwardWires is null))
                foreach (var wire in _forwardWires)
                {
                    if (wire.ToGlobalOffset == _currentGlobalOffset)
                    {
                        forwardWire = wire;
                        break;
                    }

                    i++;
                }

            // If there is a forward wiring we need to resolve it
            if (i < _forwardWires.Count)
            {
                _forwardWires.RemoveAt(i);

                // The forward wire ends up in the same island
                if (ReferenceEquals(forwardWire.Phase, _currentPhase))
                    forwardWire.Phase.AssignPort(forwardWire.From, localSlot, forwardWire.OutStage);
                else
                {
                    var publisher = forwardWire.Phase.CreatePublisher(forwardWire.From, forwardWire.OutStage);
                    _currentPhase.TakePublisher(localSlot, publisher);
                }
            }

            _currentGlobalOffset++;
        }

        internal void WireOut(OutPort output, int absoluteOffset, object logic)
        {
            //          <-------- backwards, visited stuff
            //                   ------------> forward, not yet visited
            // ---------------- .. (we are here)
            // First check if we are wiring backwards. This is important since we can only do resolution for backward wires.
            // In other cases we need to record the forward wire and resolve it later once its target inSlot has been visited.
            if (absoluteOffset < _currentGlobalOffset)
            {

            }
            else
            {
                // We need to record the forward wiring so we can resolve it later

                // The forward wire tracking data structure is only allocated when needed. Many graphs have no forward wires
                // even though it might have islands.
                if (_forwardWires is null)
                    _forwardWires = new List<ForwardWire>(8);

                var forwardWire = new ForwardWire(
                    islandGlobalOffset: _currentIslandGlobalOffset,
                    from: output,
                    toGlobalOffset: absoluteOffset,
                    outStage: logic,
                    phase: _currentPhase);

                _forwardWires.Add(forwardWire);
            }
        }

        internal void AllNestedIslandsReady()
        {
            if (!(_activePhases is null))
                foreach (var phase in _activePhases)
                {
                    phase.OnIslandReady();
                }
        }
    }

    internal enum IslandTag
    {
        GraphStage,
        SourceModuleIsland,
        SinkModuleIsland,
        ProcessorModuleIsland,
        TlsModuleIsland
    }

    /// <summary>
    /// Ephemeral type for <see cref="IPhase{T}"/>.
    /// </summary>
    /// <seealso cref="IPhase{T}"/>
    internal interface IPhase
    {
        IPhaseIsland Apply(ActorMaterializerSettings settings,
            Attributes effectiveAttributes,
            PhasedFusingActorMaterializer materializer,
            string islandName);
    }

    internal interface IPhase<T> : IPhase
    {
        IPhaseIsland<T> Apply(ActorMaterializerSettings settings,
            Attributes effectiveAttributes,
            PhasedFusingActorMaterializer materializer,
            string islandName);
    }

    /// <summary>
    /// Ephemeral type for <see cref="IPhaseIsland{T}"/>.
    /// </summary>
    /// <seealso cref="IPhaseIsland{T}"/>
    internal interface IPhaseIsland
    {
        string Name { get; }
        void AssignPort(InPort input, int localSlot, object logic);
        void AssignPort(OutPort output, int localSlot, object logic);
        Reactive.Streams.IPublisher<object> CreatePublisher(OutPort output, object logic);
        void TakePublisher(int slot, Reactive.Streams.IPublisher<object> publisher);
        void OnIslandReady();
    }

    internal interface IPhaseIsland<TLogic> : IPhaseIsland
    {
        (TLogic, TMat) MaterializeAtomic<TShape, TMat>(StreamLayout.IAtomicModule<TShape, TMat> module, Attributes attributes) where TShape : Shape;
        Reactive.Streams.IPublisher<object> CreatePublisher(OutPort output, TLogic logic);
        void AssignPort(InPort input, int localSlot, TLogic logic);
        void AssignPort(OutPort output, int localSlot, TLogic logic);
    }

    internal sealed class GraphStageIsland : IPhaseIsland<GraphStageLogic>
    {
        private readonly ActorMaterializerSettings _settings;
        private readonly Attributes _effectiveAttributes;
        private readonly PhasedFusingActorMaterializer _materializer;
        private readonly string _islandName;
        private readonly Func<GraphInterpreterShell, IActorRef> _subFlowFuser;
        private readonly List<GraphStageLogic> _logics = new List<GraphStageLogic>(16);

        private GraphInterpreter.Connection[] _connections = new GraphInterpreter.Connection[16];
        private int _maxConnections = 0;
        private List<GraphInterpreter.Connection> _outConnections = new List<GraphInterpreter.Connection>(0);
        private string _fullIslandName = null;

        private readonly GraphInterpreterShell _shell;

        internal GraphStageIsland(ActorMaterializerSettings settings, Attributes effectiveAttributes, PhasedFusingActorMaterializer materializer, string islandName, Func<GraphInterpreterShell, IActorRef> subFlowFuser = null)
        {
            _settings = settings;
            _effectiveAttributes = effectiveAttributes;
            _materializer = materializer;
            _islandName = islandName;
            _subFlowFuser = subFlowFuser;
            _shell = new GraphInterpreterShell(connections: null, logics: null, settings: settings, effectiveAttributes: effectiveAttributes, materializer: materializer);
        }

        public (GraphStageLogic, TMat) MaterializeAtomic<TShape, TMat>(StreamLayout.IAtomicModule<TShape, TMat> module, Attributes attributes) where TShape : Shape
        {
            // TODO: bail on unknown types
            var stageModule = module as GraphStageModule<TShape, TMat>;
            var stage = stageModule.Stage;
            var matAndLogic = stage.CreateLogicAndMaterializedValue(attributes, _materializer);
            var logic = matAndLogic.Item1;
            logic.OriginalStage = stage;
            logic.Attributes = attributes;
            _logics.Add(logic);
            logic.StageId = _logics.Count - 1;
            if (_fullIslandName is null)
            {
                _fullIslandName = _islandName + "-" + logic.Attributes.NameOrDefault();
            }

            return matAndLogic;
        }

        public string Name => "Fusing GraphStages phase";


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(InPort input, int slot, object logic) =>
            AssignPort(input, slot, (GraphStageLogic)logic);

        public void AssignPort(InPort input, int slot, GraphStageLogic logic)
        {
            var connection = Connection(slot);
            connection.InOwner = logic;
            connection.Id = slot;
            connection.InHandler = logic.Handlers[input.Id] as IInHandler;
            if (connection.InHandler is null) FailOnMissingHandler(logic);
            logic.PortToConn[input.Id] = connection;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(OutPort output, int slot, object logic) =>
            AssignPort(output, slot, (GraphStageLogic)logic);

        public void AssignPort(OutPort output, int slot, GraphStageLogic logic)
        {
            var connection = Connection(slot);
            connection.OutOwner = logic;
            connection.Id = slot;
            connection.OutHandler = logic.Handlers[output.Id] as IOutHandler;
            if (connection.OutHandler is null) FailOnMissingHandler(logic);
            logic.PortToConn[output.Id] = connection;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IPublisher<object> IPhaseIsland.CreatePublisher(OutPort output, object logic) => this.CreatePublisher(output, (GraphStageLogic)logic);

        public IPublisher<object> CreatePublisher(OutPort output, GraphStageLogic logic)
        {
            var boundary = new ActorGraphInterpreter.ActorOutputBoundary(_shell, output.ToString());
            _logics.Add(boundary);
            boundary.StageId = _logics.Count - 1;

            var connection = OutConnection();
            boundary.PortToConn[boundary.In.Id] = connection;
            connection.InHandler = boundary.Handlers[0] as IInHandler;
            connection.InOwner = boundary;
            connection.OutOwner = logic;
            connection.Id = -1; // will be filled later
            connection.OutHandler = logic.Handlers[logic.InCount + output.Id] as IOutHandler;
            if (connection.OutHandler is null) FailOnMissingHandler(logic);
            logic.PortToConn[logic.InCount + output.Id] = connection;

            return boundary.Publisher;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FailOnMissingHandler(GraphStageLogic logic)
        {
            var missingHandlerIdx = -1;
            for (int i = 0; i < logic.Handlers.Length; i++)
            {
                if (logic.Handlers[i] is null)
                {
                    missingHandlerIdx = i;
                    break;
                }
            }

            var isIn = missingHandlerIdx < logic.InCount;
            string portLabel;
            if (logic.OriginalStage is null)
                if (isIn) portLabel = $"in port id [{missingHandlerIdx}]";
                else portLabel = $"out port id [{missingHandlerIdx}]";
            else
                if (isIn) portLabel = $"in port [{logic.OriginalStage.Inlets[missingHandlerIdx]}]";
            else portLabel = $"out port [{logic.OriginalStage.Inlets[missingHandlerIdx]}]";

            throw new IllegalStateException($"No handler defined in stage [{logic.OriginalStage ? ToString() ?? logic.ToString()}] for {portLabel}. All inlets and outlets must be assigned a handler with setHandler in the constructor of your graph stage logic.");
        }

        public void TakePublisher(int slot, IPublisher<object> publisher)
        {
            var connection = Connection(slot);
            // TODO: proper input port debug string (currently prints the stage)
            var bufferSize = connection.InOwner.Attributes.MandatoryAttribute<Attributes.InputBuffer>().Max;
            var boundary = new ActorGraphInterpreter.BatchingActorInputBoundary(bufferSize, _shell, publisher, connection.InOwner.ToString());
            _logics.Add(boundary);
            boundary.StageId = _logics.Count - 1;

            boundary.PortToConn(boundary.Out.Id + boundary.InCount) = connection;
            connection.OutHandler = boundary.Handlers[0] as IOutHandler;
            connection.OutOwner = boundary;
        }

        public void OnIslandReady()
        {
            var totalConnections = _maxConnections + _outConnections.Count + 1;
            var finalConnections = new GraphInterpreter.Connection[totalConnections];
            Array.Copy(_connections, finalConnections, totalConnections);
            
            var i = _maxConnections + 1;
            var outConn = _outConnections;
            while (i < totalConnections)
            {
                var conn = _outConnections[0];
                _outConnections.RemoveAt(0);
                if (conn.InHandler is null) FailOnMissingHandler(conn.InOwner);
                else if (conn.OutHandler is null) FailOnMissingHandler(conn.OutOwner);
                finalConnections[i] = conn;
                conn.Id = i;
                i++;
            }
            
            _shell.Connections = finalConnections;
            _shell.Logics = _logics.ToArray(_logicArrayType);
            
            if (_subFlowFuser is null)
            {
                var props = ActorGraphInterpreter.Props(_shell).WithDispatcher(ActorAttributes.Dispatcher.Resolve(_effectiveAttributes, _settings));
                var actorName = _fullIslandName ?? _islandName;
                var arf = _materializer.ActorOf(props, actorName);
            }
            else
            {
                _subFlowFuser(_shell);
            }
        }

        public GraphInterpreter.Connection Connection(int slot)
        {
            _maxConnections = Math.Max(slot, _maxConnections);
            if (_maxConnections >= _connections.Length)
            {
                var connections = new GraphInterpreter.Connection[_connections.Length * 2];
                Array.Copy(_connections, connections, _connections.Length);
                _connections = connections;
            }

            var c = _connections[slot];
            if (c is null)
            {
                c = new GraphInterpreter.Connection(0, 0, null, 0, null, null, null);
                _connections[slot] = c;
            }

            return c;
        }

        public GraphInterpreter.Connection OutConnection()
        {
            var connection = new GraphInterpreter.Connection(0, 0, null, 0, null, null, null);
            _outConnections.Add(connection);
            return connection;
        }

        public override string ToString() => $"GraphStagePhase";
    }

    internal sealed class SourceModulePhase<T> : IPhaseIsland<IPublisher<T>>
    {
        private readonly PhasedFusingActorMaterializer _materializer;
        private readonly string _islandName;

        public SourceModulePhase(PhasedFusingActorMaterializer materializer, string islandName)
        {
            _materializer = materializer;
            _islandName = islandName;
        }

        public string Name => "SinkModule phase";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(InPort input, int localSlot, object logic) =>
            this.AssignPort(input, localSlot, (IPublisher<object>)logic);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(OutPort output, int localSlot, object logic) =>
            this.AssignPort(output, localSlot, (IPublisher<object>) logic);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IPublisher<object> IPhaseIsland.CreatePublisher(OutPort output, object logic) =>
            this.CreatePublisher(output, (IPublisher<object>)logic);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TakePublisher(int slot, IPublisher<T> publisher)
        {
            throw new NotSupportedException("A Source cannot take a Publisher");
        }

        public void OnIslandReady()
        {
        }

        public (IPublisher<T>, TMat) MaterializeAtomic<TShape, TMat>(StreamLayout.IAtomicModule<TShape, TMat> module, Attributes attributes) where TShape : Shape
        {
            if (module is SourceModule<T, TMat> sourceModule)
            {
                return sourceModule.Create(new MaterializationContext(_materializer, attributes, _islandName + "-" + attributes.GetNameOrDefault()));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPublisher<object> CreatePublisher(OutPort output, IPublisher<object> logic) => logic;

        public void AssignPort(InPort input, int localSlot, IPublisher<object> logic) { }

        public void AssignPort(OutPort output, int localSlot, IPublisher<object> logic) { }
    }

    internal sealed class SinkModulePhase<T> : IPhaseIsland<object>
    {
        private readonly PhasedFusingActorMaterializer _materializer;
        private readonly string _islandName;
        private object _subscriberOrVirtualPublisher;

        public SinkModulePhase(PhasedFusingActorMaterializer materializer, string islandName)
        {
            _materializer = materializer;
            _islandName = islandName;
        }

        public string Name => "SinkModule phase";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(InPort input, int localSlot, object logic) =>
            this.AssignPort(input, localSlot, (IPublisher<object>)logic);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IPhaseIsland.AssignPort(OutPort output, int localSlot, object logic) =>
            this.AssignPort(output, localSlot, (IPublisher<object>)logic);

        [MethodImpl(MethodImplOptions.NoInlining)]
        IPublisher<object> IPhaseIsland.CreatePublisher(OutPort output, object logic) =>
            throw new NotSupportedException("A Sink cannot take a Publisher");

        public void TakePublisher(int slot, IPublisher<T> publisher)
        {
            if (_subscriberOrVirtualPublisher is VirtualPublisher<T> v) v.RegisterPublisher(publisher);
            else if (_subscriberOrVirtualPublisher is ISubscriber<T> s) publisher.Subscribe(s);
        }

        public void OnIslandReady()
        {
        }

        public (object, TMat) MaterializeAtomic<TShape, TMat>(StreamLayout.IAtomicModule<TShape, TMat> module, Attributes attributes) where TShape : Shape
        {
            if (module is SinkModule<T, TMat> sourceModule)
            {
                var subAndMat = sourceModule.Create(new MaterializationContext(_materializer, attributes, _islandName + "-" + attributes.GetNameOrDefault()));
                _subscriberOrVirtualPublisher = subAndMat.Item1;
                return subAndMat;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPublisher<object> CreatePublisher(OutPort output, IPublisher<object> logic) => logic;

        public void AssignPort(InPort input, int localSlot, IPublisher<object> logic) { }

        public void AssignPort(OutPort output, int localSlot, IPublisher<object> logic) { }
    }
}