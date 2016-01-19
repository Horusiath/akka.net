﻿using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Streams.Stage;

namespace Akka.Streams.Dsl
{
    /// <summary>
    /// Merge several streams, taking elements as they arrive from input streams
    /// (picking randomly when several have elements ready).
    /// <para>
    /// '''Emits when''' one of the inputs has an element available
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' all upstreams complete
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary>
    public sealed class Merge<T> : GraphStage<UniformFanInShape<T, T>>
    {
        #region graph stage logic

        private sealed class MergeStageLogic : GraphStageLogic
        {
            private readonly Merge<T> _stage;
            private readonly Inlet<T>[] _pendingQueue;

            private int _runningUpstreams;
            private int _pendingHead = 0;
            private int _pendingTail = 0;
            private bool _initialized = false;

            public MergeStageLogic(Shape shape, Merge<T> stage) : base(shape)
            {
                _stage = stage;
                _runningUpstreams = _stage._inputPorts;
                _pendingQueue = new Inlet<T>[_stage._ins.Length];

                var outlet = _stage._out;
                foreach (var inlet in _stage._ins)
                {
                    SetHandler(inlet, onPush: () =>
                    {
                        if (IsAvailable(outlet))
                        {
                            if (!IsPending)
                            {
                                Push(outlet, Grab(inlet));
                                TryPull(inlet);
                            }
                        }
                        else Enqeue(inlet);
                    },
                    onUpstreamFinish: () =>
                    {
                        if (_stage._eagerClose)
                        {
                            foreach (var i in _stage._ins) Cancel(i);
                            _runningUpstreams = 0;
                            if (!IsPending) CompleteStage<T>();
                        }
                        else
                        {
                            _runningUpstreams--;
                            if (IsUpstreamClosed && !IsPending) CompleteStage<T>();
                        }
                    });
                }

                SetHandler(outlet, onPull: () =>
                {
                    if (IsPending) DequeueAndDispatch();
                });
            }

            private bool IsUpstreamClosed { get { return _runningUpstreams == 0; } }
            private bool IsPending { get { return _pendingHead != _pendingTail; } }

            public override void PreStart()
            {
                foreach (var inlet in _stage._ins) TryPull(inlet);
            }

            private void Enqeue(Inlet<T> inlet)
            {
                _pendingQueue[_pendingTail % _stage._inputPorts] = inlet;
                _pendingTail++;
            }

            private void DequeueAndDispatch()
            {
                var inlet = _pendingQueue[_pendingHead % _stage._inputPorts];
                _pendingHead++;
                Push(_stage._out, Grab(inlet));
                if (IsUpstreamClosed && !IsPending) CompleteStage<T>();
                else TryPull(inlet);
            }
        }

        #endregion

        private readonly int _inputPorts;
        private readonly bool _eagerClose;

        private readonly Inlet<T>[] _ins;
        private readonly Outlet<T> _out = new Outlet<T>("Merge.out");

        public Merge(int inputPorts, bool eagerClose = false)
        {
            if (inputPorts <= 1) throw new ArgumentException("Merge must have more than 1 input port");
            _inputPorts = inputPorts;
            _eagerClose = eagerClose;

            _ins = new Inlet<T>[inputPorts];
            for (int i = 0; i < inputPorts; i++)
                _ins[i] = new Inlet<T>("Merge.in" + i);

            Shape = new UniformFanInShape<T, T>(_out, _ins);
            InitialAttributes = Attributes.CreateName("Merge");
        }

        protected override Attributes InitialAttributes { get; }
        public override UniformFanInShape<T, T> Shape { get; }
        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new MergeStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Merge several streams, taking elements as they arrive from input streams
    /// (picking from preferred when several have elements ready).
    /// 
    /// A <see cref="MergePreferred{T}"/> has one `out` port, one `preferred` input port and 0 or more secondary `in` ports.
    /// <para>
    /// '''Emits when''' one of the inputs has an element available, preferring
    /// a specified input if multiple have elements available
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' all upstreams complete (eagerClose=false) or one upstream completes (eagerClose=true)
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// <para>
    /// A `Broadcast` has one `in` port and 2 or more `out` ports.
    /// </para>
    /// </summary>
    public sealed class MergePreferred<T> : GraphStage<MergePreferred<T>.MergePreferredShape>
    {
        #region internal classes

        public sealed class MergePreferredShape : UniformFanInShape<T, T>
        {
            private readonly int _secondaryPorts;
            private readonly IInit _init;

            public MergePreferredShape(int secondaryPorts, IInit init) : base(secondaryPorts, init)
            {
                _secondaryPorts = secondaryPorts;
                _init = init;

                Preferred = NewInlet<T>("preferred");
            }

            public MergePreferredShape(int secondaryPorts, string name) : this(secondaryPorts, new InitName(name)) { }

            protected override FanInShape<T> Construct(IInit init)
            {
                return new MergePreferredShape(_secondaryPorts, init);
            }

            public Inlet<T> Preferred { get; }
        }

        private sealed class MergePreferredStageLogic : GraphStageLogic
        {
            /// <summary>
            /// This determines the unfairness of the merge:
            /// - at 1 the preferred will grab 40% of the bandwidth against three equally fast secondaries
            /// - at 2 the preferred will grab almost all bandwidth against three equally fast secondaries
            /// (measured with eventLimit=1 in the GraphInterpreter, so may not be accurate)
            /// </summary>
            public const int MaxEmitting = 2;
            private readonly MergePreferred<T> _stage;
            private readonly Action[] _pullMe;
            private int _openInputs;
            private int _preferredEmitting = 0;
            private bool _isFirst = true;

            public MergePreferredStageLogic(Shape shape, MergePreferred<T> stage) : base(shape)
            {
                _stage = stage;
                _openInputs = stage._secondaryPorts + 1;
                _pullMe = new Action[stage._secondaryPorts];
                for (int i = 0; i < stage._secondaryPorts; i++)
                {
                    var inlet = stage.In(i);
                    _pullMe[i] = () => TryPull(inlet);
                }

                SetHandler(_stage.Out, onPull: () =>
                {
                    if (_isFirst)
                    {
                        _isFirst = false;
                        TryPull(_stage.Preferred);
                        foreach (var inlet in _stage.Shape.Inlets.Cast<Inlet<T>>())
                            TryPull(inlet);
                    }
                });

                SetHandler(_stage.Preferred,
                    onUpstreamFinish: OnComplete,
                    onPush: () =>
                    {
                        if (_preferredEmitting == MaxEmitting) { /* blocked */ }
                        else EmitPreferred();
                    });

                for (int i = 0; i < stage._secondaryPorts; i++)
                {
                    var port = stage.In(i);
                    var pullPort = _pullMe[i];

                    SetHandler(port, onPush: () =>
                    {
                        if (_preferredEmitting > 0) { /* blocked */ }
                        else Emit(_stage.Out, Grab(port), pullPort);
                    },
                    onUpstreamFinish: OnComplete);
                }
            }

            private void OnComplete()
            {
                _openInputs--;
                if (_stage._eagerClose || _openInputs == 0) CompleteStage<T>();
            }

            private void EmitPreferred()
            {
                _preferredEmitting++;
                Emit(_stage.Out, Grab(_stage.Preferred), Emitted);
                TryPull(_stage.Preferred);
            }

            private void Emitted()
            {
                _preferredEmitting--;
                if (IsAvailable(_stage.Preferred)) EmitPreferred();
                else if (_preferredEmitting == 0) EmitSecondary();
            }

            private void EmitSecondary()
            {
                for (int i = 0; i < _stage._secondaryPorts; i++)
                {
                    var port = _stage.In(i);
                    if (IsAvailable(port)) Emit(_stage.Out, Grab(port), _pullMe[i]);
                }
            }
        }

        #endregion

        private readonly int _secondaryPorts;
        private readonly bool _eagerClose;

        public MergePreferred(int secondaryPorts, bool eagerClose = false)
        {
            if (secondaryPorts < 1) throw new ArgumentException("A MergePreferred must have at least one secondary port");
            _secondaryPorts = secondaryPorts;
            _eagerClose = eagerClose;

            Shape = new MergePreferredShape(_secondaryPorts, "MergePreferred");
            InitialAttributes = Attributes.CreateName("MergePreferred");
        }

        protected override Attributes InitialAttributes { get; }
        public override MergePreferredShape Shape { get; }

        public Inlet<T> In(int id)
        {
            return Inlet.Create<T>(Shape.Inlets.ElementAt(id));
        }

        public Outlet<T> Out { get { return Shape.Out; } }
        public Inlet<T> Preferred { get { return Shape.Preferred; } }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new MergePreferredStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Interleave represents deterministic merge which takes N elements per input stream,
    /// in-order of inputs, emits them downstream and then cycles/"wraps-around" the inputs.
    /// <para>
    /// '''Emits when''' element is available from current input (depending on phase)
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' all upstreams complete (eagerClose=false) or one upstream completes (eagerClose=true)
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary> 
    public sealed class Interleave<T> : GraphStage<UniformFanInShape<T, T>>
    {
        #region stage logic
        private sealed class InterleaveStageLogic : GraphStageLogic
        {
            private readonly Interleave<T> _stage;
            private int _counter = 0;
            private int _currentUpstreamIndex = 0;
            private int _runningUpstreams;

            public InterleaveStageLogic(Shape shape, Interleave<T> stage) : base(shape)
            {
                _stage = stage;
                _runningUpstreams = _stage._inputPorts;

                foreach (var inlet in _stage.Inlets)
                {
                    SetHandler(inlet, onPush: () =>
                    {
                        Push(_stage.Out, Grab(inlet));
                        _counter++;
                        if (_counter == _stage._segmentSize) SwitchToNextInput();
                    },
                    onUpstreamFinish: () =>
                    {
                        if (!_stage._eagerClose)
                        {
                            _runningUpstreams--;
                            if (!IsUpstreamClosed)
                            {
                                if (Equals(inlet, CurrentUpstream))
                                {
                                    SwitchToNextInput();
                                    if (IsAvailable(_stage.Out)) Pull(CurrentUpstream);
                                }
                            }
                            else CompleteStage<T>();
                        }
                        else CompleteStage<T>();
                    });
                }

                SetHandler(_stage.Out, onPull: () =>
                {
                    if (!HasBeenPulled(CurrentUpstream)) TryPull(CurrentUpstream);
                });
            }

            private bool IsUpstreamClosed { get { return _runningUpstreams == 0; } }
            private Inlet<T> CurrentUpstream { get { return _stage.Inlets[_currentUpstreamIndex]; } }

            private void SwitchToNextInput()
            {
                _counter = 0;
                var index = _currentUpstreamIndex;
                while (true)
                {
                    var successor = (index + 1) % _stage._inputPorts;
                    if (!IsClosed(_stage.Inlets[successor])) _currentUpstreamIndex = successor;
                    else
                    {
                        if (successor != _currentUpstreamIndex)
                        {
                            index = successor;
                            continue;
                        }
                        else
                        {
                            CompleteStage<T>();
                            _currentUpstreamIndex = 0;
                        }
                    }

                    break;
                }
            }
        }
        #endregion

        private readonly int _inputPorts;
        private readonly int _segmentSize;
        private readonly bool _eagerClose;

        public Interleave(int inputPorts, int segmentSize, bool eagerClose = false)
        {
            if (inputPorts <= 1) throw new ArgumentException("Interleave input ports count must be greater than 1", "inputPorts");
            if (segmentSize <= 0) throw new ArgumentException("Interleave segment size must be greater than 0", "segmentSize");

            _inputPorts = inputPorts;
            _segmentSize = segmentSize;
            _eagerClose = eagerClose;

            Out = new Outlet<T>("Interleave.out");
            Inlets = new Inlet<T>[inputPorts];
            for (int i = 0; i < inputPorts; i++)
                Inlets[i] = new Inlet<T>("Interleave.in" + i);

            Shape = new UniformFanInShape<T, T>(Out, Inlets);
        }

        public Outlet<T> Out { get; }
        public Inlet<T>[] Inlets { get; }

        public override UniformFanInShape<T, T> Shape { get; }
        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new InterleaveStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Merge two pre-sorted streams such that the resulting stream is sorted.
    /// <para>
    /// '''Emits when''' both inputs have an element available
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' all upstreams complete
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary>
    public sealed class MergeSorted<T> : GraphStage<FanInShape<T, T, T>> where T : IComparable<T>
    {
        #region stage logic
        private sealed class MergeSortedStageLogic : GraphStageLogic
        {
            private readonly MergeSorted<T> _stage;
            private T _other;

            readonly Action<T> DispatchRight;
            readonly Action<T> DispatchLeft;
            readonly Action PassRight;
            readonly Action PassLeft;
            readonly Action ReadRight;
            readonly Action ReadLeft;

            public MergeSortedStageLogic(Shape shape, MergeSorted<T> stage) : base(shape)
            {
                _stage = stage;
                DispatchRight = right => Dispatch(_other, right);
                DispatchLeft = left => Dispatch(left, _other);
                PassRight = () => Emit(_stage.Out, _other, () =>
                {
                    NullOut();
                    PassAlong(_stage.Right, _stage.Out, doPull: true);
                });
                PassLeft = () => Emit(_stage.Out, _other, () =>
                {
                    NullOut();
                    PassAlong(_stage.Left, _stage.Out, doPull: true);
                });
                ReadRight = () => Read(_stage.Right, DispatchRight, PassLeft);
                ReadLeft = () => Read(_stage.Left, DispatchLeft, PassRight);

                SetHandler(_stage.Left, IgnoreTerminateInput);
                SetHandler(_stage.Right, IgnoreTerminateInput);
                SetHandler(_stage.Out, EagerTerminateOutput);
            }

            public override void PreStart()
            {
                // all fan-in stages need to eagerly pull all inputs to get cycles started
                Pull(_stage.Right);
                Read(_stage.Left, left =>
                {
                    _other = left;
                },
                () => PassAlong(_stage.Right, _stage.Out));
            }

            private void NullOut()
            {
                _other = default(T);
            }

            private void Dispatch(T left, T right)
            {
                if (left.CompareTo(right) == -1)
                {
                    _other = right;
                    Emit(_stage.Out, left, ReadLeft);
                }
                else
                {
                    _other = left;
                    Emit(_stage.Out, right, ReadRight);
                }
            }
        }
        #endregion

        public readonly Inlet<T> Left = new Inlet<T>("left");
        public readonly Inlet<T> Right = new Inlet<T>("right");
        public readonly Outlet<T> Out = new Outlet<T>("out");

        public MergeSorted()
        {
            Shape = new FanInShape<T, T, T>(Out, Left, Right);
        }

        public override FanInShape<T, T, T> Shape { get; }
        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new MergeSortedStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Fan-out the stream to several streams emitting each incoming upstream element to all downstream consumers.
    /// It will not shut down until the subscriptions for at least two downstream subscribers have been established.
    /// <para>
    /// '''Emits when''' all of the outputs stops backpressuring and there is an input element available
    /// </para>
    /// '''Backpressures when''' any of the outputs backpressure
    /// <para>
    /// '''Completes when''' upstream completes
    /// </para>
    /// '''Cancels when''' If eagerCancel is enabled: when any downstream cancels; otherwise: when all downstreams cancel
    /// </summary>
    public sealed class Broadcast<T> : GraphStage<UniformFanOutShape<T, T>>
    {
        #region stage logic
        private sealed class BroadcastStageLogic : GraphStageLogic
        {
            private readonly Broadcast<T> _stage;
            private readonly bool[] _pending;
            private int _pendingCount;
            private int _downstreamRunning;

            public BroadcastStageLogic(Shape shape, Broadcast<T> stage) : base(shape)
            {
                _stage = stage;
                _pendingCount = _downstreamRunning = stage._outputPorts;
                _pending = new bool[stage._outputPorts];
                for (int i = 0; i < stage._outputPorts; i++) _pending[i] = true;

                SetHandler(_stage.In, onPush: () =>
                {
                    _pendingCount = _downstreamRunning;
                    var element = Grab(stage.In);
                    var idx = 0;
                    var enumerator = (stage.Out as IEnumerable<Outlet>).GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var o = (Outlet<T>)enumerator.Current;
                        if (!IsClosed(o))
                        {
                            Push(o, element);
                            _pending[idx] = true;
                        }
                        idx++;
                    }
                });

                var outIdx = 0;
                var outEnumerator = (stage.Out as IEnumerable<Outlet>).GetEnumerator();
                while (outEnumerator.MoveNext())
                {
                    var o = (Outlet<T>)outEnumerator.Current;
                    var i = outIdx;
                    SetHandler(o, onPull: () =>
                    {
                        _pending[i] = false;
                        _pendingCount--;
                        TryPull();
                    },
                    onDownstreamFinish: () =>
                    {
                        if (stage._eagerCancel) CompleteStage<T>();
                        else
                        {
                            _downstreamRunning--;
                            if (_downstreamRunning == 0) CompleteStage<T>();
                            else if (_pending[i])
                            {
                                _pending[i] = false;
                                _pendingCount--;
                                TryPull();
                            }
                        }
                    });
                    outIdx++;
                }
            }

            private void TryPull()
            {
                if (_pendingCount == 0 && !HasBeenPulled(_stage.In)) Pull(_stage.In);
            }
        }
        #endregion

        private readonly int _outputPorts;
        private readonly bool _eagerCancel;

        public readonly Inlet<T> In = new Inlet<T>("Broadcast.in");
        public readonly Outlet<T>[] Out;

        public Broadcast(int outputPorts, bool eagerCancel = false)
        {
            if (outputPorts <= 1) throw new ArgumentException("Broadcast require more than 1 output port", "outputPorts");
            _outputPorts = outputPorts;
            _eagerCancel = eagerCancel;

            Out = new Outlet<T>[outputPorts];
            for (int i = 0; i < outputPorts; i++)
                Out[i] = new Outlet<T>("Broadcast.out" + i);

            Shape = new UniformFanOutShape<T, T>(In, Out);
            InitialAttributes = Attributes.CreateName("Broadcast");
        }

        protected override Attributes InitialAttributes { get; }
        public override UniformFanOutShape<T, T> Shape { get; }
        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new BroadcastStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Fan-out the stream to several streams. Each upstream element is emitted to the first available downstream consumer.
    /// It will not shut down until the subscriptions
    /// for at least two downstream subscribers have been established.
    /// 
    /// A <see cref="Balance{T}"/> has one <see cref="In"/> port and 2 or more <see cref="Outs"/> ports.
    /// <para>
    /// '''Emits when''' any of the outputs stops backpressuring; emits the element to the first available output
    /// </para>
    /// '''Backpressures when''' all of the outputs backpressure
    /// <para>
    /// '''Completes when''' upstream completes
    /// </para>
    /// '''Cancels when''' all downstreams cancel
    /// </summary>
    public sealed class Balance<T> : GraphStage<UniformFanOutShape<T, T>>
    {
        #region stage logic
        private sealed class BalanceStageLogic : GraphStageLogic
        {
            private readonly Balance<T> _stage;
            private readonly Outlet<T>[] _pendingQueue;
            private int _pendingHead = 0;
            private int _pendingTail = 0;
            private int _needDownstreamPulls = 0;
            private int _downstreamsRunning;
            public BalanceStageLogic(Shape shape, Balance<T> stage) : base(shape)
            {
                _stage = stage;
                _pendingQueue = new Outlet<T>[_stage._outputPorts];
                _downstreamsRunning = _stage._outputPorts;

                if (_stage._waitForAllDownstreams) _needDownstreamPulls = _stage._outputPorts;

                SetHandler(_stage.In, onPush: DequeueAndDispatch);

                foreach (var outlet in _stage.Outs)
                {
                    var hasPulled = false;
                    SetHandler(outlet, onPull: () =>
                    {
                        if (!hasPulled)
                        {
                            hasPulled = false;
                            if (_needDownstreamPulls > 0) _needDownstreamPulls--;
                        }

                        if (_needDownstreamPulls == 0)
                        {
                            if (IsAvailable(_stage.In))
                            {
                                if (NoPending) Push(outlet, Grab(_stage.In));
                            }
                            else
                            {
                                if (!HasBeenPulled(_stage.In)) Pull(_stage.In);
                                Enqueue(outlet);
                            }
                        }
                        else Enqueue(outlet);
                    },
                    onDownstreamFinish: () =>
                    {
                        _downstreamsRunning--;
                        if (_downstreamsRunning == 0) CompleteStage<T>();
                        else if (!hasPulled && _needDownstreamPulls > 0)
                        {
                            _needDownstreamPulls--;
                            if (_needDownstreamPulls == 0 && !HasBeenPulled(_stage.In)) Pull(_stage.In);
                        }
                    });
                }
            }

            private bool NoPending { get { return _pendingHead == _pendingTail; } }

            private void Enqueue(Outlet<T> outlet)
            {
                _pendingQueue[_pendingTail % _stage._outputPorts] = outlet;
                _pendingTail++;
            }

            private void DequeueAndDispatch()
            {
                var outlet = _pendingQueue[_pendingHead % _stage._outputPorts];
                _pendingHead++;
                Push(outlet, Grab(_stage.In));
                if (!NoPending) Pull(_stage.In);
            }
        }
        #endregion

        private readonly int _outputPorts;
        private readonly bool _waitForAllDownstreams;

        public Balance(int outputPorts, bool waitForAllDownstreams = false)
        {
            if (outputPorts <= 1) throw new ArgumentException("A Balance must have more than 1 output ports");
            _outputPorts = outputPorts;
            _waitForAllDownstreams = waitForAllDownstreams;

            Outs = new Outlet<T>[outputPorts];
            for (int i = 0; i < outputPorts; i++) Outs[i] = new Outlet<T>("Balance.out" + i);

            InitialAttributes = Attributes.CreateName("Balance");
            Shape = new UniformFanOutShape<T, T>(In, Outs);
        }

        public Inlet<T> In { get; } = new Inlet<T>("Balance.in");
        public Outlet<T>[] Outs { get; }

        protected override Attributes InitialAttributes { get; }
        public override UniformFanOutShape<T, T> Shape { get; }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new BalanceStageLogic(Shape, this);
        }
    }

    /// <summary>
    /// Combine the elements of 2 streams into a stream of tuples.
    /// 
    /// A <see cref="Zip{T1,T2}"/> has a `left` and a `right` input port and one `out` port
    /// <para>
    /// '''Emits when''' all of the inputs has an element available
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' any upstream completes
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary>
    public sealed class Zip<T1, T2> : ZipWith<T1, T2, KeyValuePair<T1, T2>>
    {
        public Zip() : base((a, b) => new KeyValuePair<T1, T2>(a, b)) { }
    }

    /// <summary>
    /// Combine the elements of multiple streams into a stream of combined elements using a combiner function.
    /// <para>
    /// '''Emits when''' all of the inputs has an element available
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' any upstream completes
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary>
    public sealed partial class ZipWith
    {
        public static readonly ZipWith Instance = new ZipWith();
        private ZipWith() { }
    }

    /// <summary>
    /// Takes a stream of pair elements and splits each pair to two output streams.
    /// 
    /// An <see cref="UnZip{T1,T2}"/> has one `in` port and one `left` and one `right` output port.
    /// <para>
    /// '''Emits when''' all of the outputs stops backpressuring and there is an input element available
    /// </para>
    /// '''Backpressures when''' any of the outputs backpressures
    /// <para>
    /// '''Completes when''' upstream completes
    /// </para>
    /// '''Cancels when''' any downstream cancels
    /// </summary>
    public sealed class UnZip<T1, T2> : UnzipWith<KeyValuePair<T1, T2>, T1, T2>
    {
        public UnZip() : base(kv => Tuple.Create(kv.Key, kv.Value)) { }
    }

    /// <summary>
    /// Transforms each element of input stream into multiple streams using a splitter function.
    /// <para>
    /// '''Emits when''' all of the outputs stops backpressuring and there is an input element available
    /// </para>
    /// '''Backpressures when''' any of the outputs backpressures
    /// <para>
    /// '''Completes when''' upstream completes
    /// </para>
    /// '''Cancels when''' any downstream cancels
    /// </summary>
    public partial class UnzipWith
    {
        public static readonly UnzipWith Instance = new UnzipWith();
        private UnzipWith() { }
    }

    /// <summary>
    /// Takes two streams and outputs one stream formed from the two input streams
    /// by first emitting all of the elements from the first stream and then emitting
    /// all of the elements from the second stream.
    /// 
    /// A <see cref="Concat{T,TMat}"/> has one `first` port, one `second` port and one `out` port.
    /// <para>
    /// '''Emits when''' the current stream has an element available; if the current input completes, it tries the next one
    /// </para>
    /// '''Backpressures when''' downstream backpressures
    /// <para>
    /// '''Completes when''' all upstreams complete
    /// </para>
    /// '''Cancels when''' downstream cancels
    /// </summary>
    public class Concat<T> : GraphStage<UniformFanInShape<T, T>>
    {
        #region stage logic
        private sealed class ConcatStageLogic : GraphStageLogic
        {
            public ConcatStageLogic(Shape shape, Concat<T> stage) : base(shape)
            {
                var activeStream = 0;
                var iidx = 0;
                var inEnumerator = (stage.Ins as IEnumerable<Inlet>).GetEnumerator();
                while (inEnumerator.MoveNext())
                {
                    var i = (Inlet<T>)inEnumerator.Current;
                    var idx = iidx;
                    SetHandler(i,
                        onPush: () => Push(stage.Out, Grab(i)),
                        onUpstreamFinish: () =>
                        {
                            if (idx == activeStream)
                            {
                                activeStream++;
                                // skip closed inputs
                                while (activeStream < stage._inputPorts && IsClosed(stage.Ins[activeStream])) activeStream++;
                                if (activeStream == stage._inputPorts) CompleteStage<T>();
                                else if (IsAvailable(stage.Out)) Pull(stage.Ins[activeStream]);
                            }
                        });
                    iidx++;
                }

                SetHandler(stage.Out, onPull: () => Pull(stage.Ins[activeStream]));
            }
        }
        #endregion

        private readonly int _inputPorts;

        public Concat(int inputPorts = 2)
        {
            if (inputPorts <= 1) throw new ArgumentException("A Concat must have more than 1 input port");
            _inputPorts = inputPorts;

            Ins = new Inlet<T>[inputPorts];
            for (int i = 0; i < inputPorts; i++) Ins[i] = new Inlet<T>("Concat.in" + i);

            Shape = new UniformFanInShape<T, T>(Out, Ins);
            InitialAttributes = Attributes.CreateName("Concat");
        }

        public Inlet<T>[] Ins { get; }
        public Outlet<T> Out { get; } = new Outlet<T>("Concat.out");

        protected override Attributes InitialAttributes { get; }
        public override UniformFanInShape<T, T> Shape { get; }
        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new ConcatStageLogic(Shape, this);
        }
    }
}