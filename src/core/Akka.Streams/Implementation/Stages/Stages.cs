﻿using System;
using System.Collections.Generic;
using System.Reactive.Streams;
using System.Threading.Tasks;
using Akka.Event;
using Akka.Streams.Stage;
using Akka.Streams.Supervision;
using Akka.Util.Internal.Collections;

namespace Akka.Streams.Implementation.Stages
{
    internal static class DefaultAttributes
    {
        public static readonly Attributes IODispatcher = ActorAttributes.CreateDispatcher("akka.stream.default-blocking-io-dispatcher");

        public static readonly Attributes Fused = Attributes.CreateName("fused");
        public static readonly Attributes Map = Attributes.CreateName("map");
        public static readonly Attributes Log = Attributes.CreateName("log");
        public static readonly Attributes Filter = Attributes.CreateName("filter");
        public static readonly Attributes Collect = Attributes.CreateName("collect");
        public static readonly Attributes Recover = Attributes.CreateName("recover");
        public static readonly Attributes MapAsync = Attributes.CreateName("mapAsync");
        public static readonly Attributes MapAsyncUnordered = Attributes.CreateName("mapAsyncUnordered");
        public static readonly Attributes Grouped = Attributes.CreateName("grouped");
        public static readonly Attributes Sliding = Attributes.CreateName("sliding");
        public static readonly Attributes Take = Attributes.CreateName("take");
        public static readonly Attributes Drop = Attributes.CreateName("drop");
        public static readonly Attributes TakeWhile = Attributes.CreateName("takeWhile");
        public static readonly Attributes DropWhile = Attributes.CreateName("dropWhile");
        public static readonly Attributes Scan = Attributes.CreateName("scan");
        public static readonly Attributes Fold = Attributes.CreateName("fold");
        public static readonly Attributes Buffer = Attributes.CreateName("buffer");
        public static readonly Attributes Conflate = Attributes.CreateName("conflate");
        public static readonly Attributes Expand = Attributes.CreateName("expand");
        public static readonly Attributes MapConcat = Attributes.CreateName("mapConcat");
        public static readonly Attributes GroupBy = Attributes.CreateName("groupBy");
        public static readonly Attributes PrefixAndTail = Attributes.CreateName("prefixAndTail");
        public static readonly Attributes Split = Attributes.CreateName("split");
        public static readonly Attributes ConcatAll = Attributes.CreateName("concatAll");
        public static readonly Attributes Processor = Attributes.CreateName("processor");
        public static readonly Attributes ProcessorWithKey = Attributes.CreateName("processorWithKey");
        public static readonly Attributes IdentityOp = Attributes.CreateName("identityOp");

        public static readonly Attributes Merge = Attributes.CreateName("merge");
        public static readonly Attributes MergePreferred = Attributes.CreateName("mergePreferred");
        public static readonly Attributes Broadcast = Attributes.CreateName("broadcast");
        public static readonly Attributes Balance = Attributes.CreateName("balance");
        public static readonly Attributes Zip = Attributes.CreateName("zip");
        public static readonly Attributes Unzip = Attributes.CreateName("unzip");
        public static readonly Attributes Concat = Attributes.CreateName("concat");
        public static readonly Attributes Repeat = Attributes.CreateName("repeat");
        public static readonly Attributes Unfold = Attributes.CreateName("unfold");
        public static readonly Attributes UnfoldAsync = Attributes.CreateName("unfoldAsync");
        public static readonly Attributes UnfoldInf = Attributes.CreateName("unfoldInf");

        public static readonly Attributes PublisherSource = Attributes.CreateName("publisherSource");
        public static readonly Attributes EnumerableSource = Attributes.CreateName("enumerableSource");
        public static readonly Attributes FutureSource = Attributes.CreateName("futureSource");
        public static readonly Attributes TickSource = Attributes.CreateName("tickSource");
        public static readonly Attributes SingleSource = Attributes.CreateName("singleSource");
        public static readonly Attributes EmptySource = Attributes.CreateName("emptySource");
        public static readonly Attributes MaybeSource = Attributes.CreateName("maybeSource");
        public static readonly Attributes FailedSource = Attributes.CreateName("failedSource");
        public static readonly Attributes ConcatSource = Attributes.CreateName("concatSource");
        public static readonly Attributes ConcatMaterializedSource = Attributes.CreateName("concatMaterializedSource");
        public static readonly Attributes SubscriberSource = Attributes.CreateName("subscriberSource");
        public static readonly Attributes ActorPublisherSource = Attributes.CreateName("actorPublisherSource");
        public static readonly Attributes ActorRefSource = Attributes.CreateName("actorRefSource");
        public static readonly Attributes AcknowledgeSource = Attributes.CreateName("acknowledgeSource");
        public static readonly Attributes InputStreamSource = Attributes.CreateName("inputStreamSource").And(IODispatcher);
        public static readonly Attributes OutputStreamSource = Attributes.CreateName("outputStreamSource").And(IODispatcher);
        public static readonly Attributes FileSource = Attributes.CreateName("fileSource").And(IODispatcher);

        public static readonly Attributes SubscriberSink = Attributes.CreateName("subscriberSink");
        public static readonly Attributes CancelledSink = Attributes.CreateName("cancelledSink");
        public static readonly Attributes HeadSink = Attributes.CreateName("headSink").And(Attributes.CreateInputBuffer(initial: 1, max: 1));
        public static readonly Attributes HeadOrDefaultSink = Attributes.CreateName("headOrDefaultSink").And(Attributes.CreateInputBuffer(initial: 1, max: 1));
        public static readonly Attributes LastSink = Attributes.CreateName("lastSink");
        public static readonly Attributes LastOrDefaultSink = Attributes.CreateName("lastOrDefaultSink");
        public static readonly Attributes PublisherSink = Attributes.CreateName("publisherSink");
        public static readonly Attributes FanoutPublisherSink = Attributes.CreateName("fanoutPublisherSink");
        public static readonly Attributes IgnoreSink = Attributes.CreateName("ignoreSink");
        public static readonly Attributes ActorRefSink = Attributes.CreateName("actorRefSink");
        public static readonly Attributes ActorSubscriberSink = Attributes.CreateName("actorSubscriberSink");
        public static readonly Attributes QueueSink = Attributes.CreateName("queueSink");
        public static readonly Attributes InputStreamSink = Attributes.CreateName("inputStreamSink").And(IODispatcher);
        public static readonly Attributes OutputStreamSink = Attributes.CreateName("outputStreamSink").And(IODispatcher);
        public static readonly Attributes FileSink = Attributes.CreateName("fileSink").And(IODispatcher);
    }

    // FIXME: To be deprecated as soon as stream-of-stream operations are stages
    internal abstract class StageModule<TIn, TOut, TMat> : FlowModule<TIn, TOut, TMat>
    {
    }

    /// <summary>
    /// Stage that is backed by a GraphStage but can be symbolically introspected
    /// </summary>
    internal sealed class SymbolicGraphStage<TIn, TOut, TExt> : PushPullGraphStage<TIn, TOut, TExt>
    {
        public SymbolicGraphStage(ISymbolicStage<TIn, TOut> symbolicStage) : base(symbolicStage.Create, symbolicStage.Attributes)
        {
        }
    }

    internal interface ISymbolicStage<in TIn, out TOut> : IStage<TIn, TOut>
    {
        Attributes Attributes { get; }
        IStage<TIn, TOut> Create(Attributes effectiveAttributes);
    }

    internal abstract class SymbolicStage<TIn, TOut> : ISymbolicStage<TIn, TOut>
    {
        protected SymbolicStage(Attributes attributes)
        {
            Attributes = attributes;
        }

        public Attributes Attributes { get; }
        public abstract IStage<TIn, TOut> Create(Attributes effectiveAttributes);

        protected Decider Supervision(Attributes attributes)
        {
            return attributes.GetAttribute<ActorAttributes.SupervisionStrategy>(new ActorAttributes.SupervisionStrategy(Deciders.StoppingDecider)).Decider;
        }
    }

    internal sealed class Map<TIn, TOut> : SymbolicStage<TIn, TOut>
    {
        private readonly Func<TIn, TOut> _mapper;
        public Map(Func<TIn, TOut> mapper, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Map)
        {
            _mapper = mapper;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Map<TIn, TOut>(_mapper, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Log<T> : SymbolicStage<T, T>
    {
        private readonly string _name;
        private readonly Func<T, object> _extract;
        private readonly ILoggingAdapter _loggingAdapter;

        public Log(string name, Func<T, object> extract, ILoggingAdapter loggingAdapter, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Log)
        {
            _name = name;
            _extract = extract;
            _loggingAdapter = loggingAdapter;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Log<T>(_name, _extract, _loggingAdapter, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Filter<T> : SymbolicStage<T, T>
    {
        private readonly Predicate<T> _predicate;

        public Filter(Predicate<T> predicate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Filter)
        {
            _predicate = predicate;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Filter<T>(_predicate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Collect<TIn, TOut> : SymbolicStage<TIn, TOut> where TOut : class
    {
        private readonly Func<TIn, TOut> _partialFunc;

        public Collect(Func<TIn, TOut> partialFunc, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Collect)
        {
            _partialFunc = partialFunc;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Collect<TIn, TOut>(_partialFunc, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Recover<T> : SymbolicStage<T, T> where T : class
    {
        private readonly Func<Exception, T> _func;

        public Recover(Func<Exception, T> func, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Recover)
        {
            _func = func;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Recover<T>(_func);
        }
    }

    internal sealed class Grouped<T> : SymbolicStage<T, IEnumerable<T>>
    {
        private readonly int _count;

        public Grouped(int count, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Grouped)
        {
            if (_count <= 0) throw new ArgumentException("Grouped count must be greater than 0", "count");
            _count = count;
        }

        public override IStage<T, IEnumerable<T>> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Grouped<T>(_count);
        }
    }

    internal sealed class Sliding<T> : SymbolicStage<T, IEnumerable<T>>
    {
        private readonly int _count;
        private readonly int _step;

        public Sliding(int count, int step, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Sliding)
        {
            if (count <= 0) throw new ArgumentException("Sliding count must be greater than 0", "count");
            if (step <= 0) throw new ArgumentException("Sliding step must be greater than 0", "step");
            _count = count;
            _step = step;
        }

        public override IStage<T, IEnumerable<T>> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Sliding<T>(_count, _step);
        }
    }

    internal sealed class Take<T> : SymbolicStage<T, T>
    {
        private readonly long _count;

        public Take(long count, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Take)
        {
            _count = count;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Take<T>(_count);
        }
    }

    internal sealed class Drop<T> : SymbolicStage<T, T>
    {
        private readonly long _count;

        public Drop(long count, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Drop)
        {
            _count = count;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Drop<T>(_count);
        }
    }

    internal sealed class TakeWhile<T> : SymbolicStage<T, T>
    {
        private readonly Predicate<T> _predicate;

        public TakeWhile(Predicate<T> predicate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.TakeWhile)
        {
            _predicate = predicate;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.TakeWhile<T>(_predicate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class DropWhile<T> : SymbolicStage<T, T>
    {
        private readonly Predicate<T> _predicate;

        public DropWhile(Predicate<T> predicate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.DropWhile)
        {
            _predicate = predicate;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.DropWhile<T>(_predicate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Scan<TIn, TOut> : SymbolicStage<TIn, TOut>
    {
        private readonly TOut _zero;
        private readonly Func<TOut, TIn, TOut> _aggregate;

        public Scan(TOut zero, Func<TOut, TIn, TOut> aggregate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Scan)
        {
            _zero = zero;
            _aggregate = aggregate;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Scan<TIn, TOut>(_zero, _aggregate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Fold<TIn, TOut> : SymbolicStage<TIn, TOut>
    {
        private readonly TOut _zero;
        private readonly Func<TOut, TIn, TOut> _aggregate;

        public Fold(TOut zero, Func<TOut, TIn, TOut> aggregate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Fold)
        {
            _zero = zero;
            _aggregate = aggregate;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Fold<TIn, TOut>(_zero, _aggregate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Buffer<T> : SymbolicStage<T, T>
    {
        private readonly int _size;
        private readonly OverflowStrategy _overflowStrategy;

        public Buffer(int size, OverflowStrategy overflowStrategy, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Buffer)
        {
            _size = size;
            _overflowStrategy = overflowStrategy;
        }

        public override IStage<T, T> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Buffer<T>(_size, _overflowStrategy);
        }
    }

    internal sealed class Conflate<TIn, TOut> : SymbolicStage<TIn, TOut>
    {
        private readonly Func<TIn, TOut> _seed;
        private readonly Func<TOut, TIn, TOut> _aggregate;

        public Conflate(Func<TIn, TOut> seed, Func<TOut, TIn, TOut> aggregate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Conflate)
        {
            _seed = seed;
            _aggregate = aggregate;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Conflate<TIn, TOut>(_seed, _aggregate, Supervision(effectiveAttributes));
        }
    }

    internal sealed class Expand<TIn, TOut, TSeed> : SymbolicStage<TIn, TOut>
    {
        private readonly Func<TIn, TSeed> _seed;
        private readonly Func<TSeed, Tuple<TOut, TSeed>> _extrapolate;

        public Expand(Func<TIn, TSeed> seed, Func<TSeed, Tuple<TOut, TSeed>> extrapolate, Attributes attributes = null) : base(attributes ?? DefaultAttributes.Expand)
        {
            _seed = seed;
            _extrapolate = extrapolate;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.Expand<TIn, TOut, TSeed>(_seed, _extrapolate);
        }
    }

    internal sealed class MapConcat<TIn, TOut> : SymbolicStage<TIn, TOut>
    {
        private readonly Func<TIn, IEnumerable<TOut>> _func;

        public MapConcat(Func<TIn, IEnumerable<TOut>> func, Attributes attributes = null) : base(attributes ?? DefaultAttributes.MapConcat)
        {
            _func = func;
        }

        public override IStage<TIn, TOut> Create(Attributes effectiveAttributes)
        {
            return new Fusing.MapConcat<TIn, TOut>(_func, Supervision(effectiveAttributes));
        }
    }

    // FIXME: These are not yet proper stages, therefore they use the deprecated StageModule infrastructure

    internal sealed class GroupBy<TIn, TOut, TMat> : StageModule<TIn, TOut, TMat>
    {
        private readonly int _maxSubstreams;
        private readonly Func<TIn, TOut> _groupFunc;

        public GroupBy(int maxSubstreams, Func<TIn, TOut> groupFunc, Attributes attributes = null)
        {
            _maxSubstreams = maxSubstreams;
            _groupFunc = groupFunc;
            Attributes = attributes ?? DefaultAttributes.GroupBy;
        }

        public override IModule CarbonCopy()
        {
            return new GroupBy<TIn, TOut, TMat>(_maxSubstreams, _groupFunc, Attributes);
        }

        public override Attributes Attributes { get; }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new GroupBy<TIn, TOut, TMat>(_maxSubstreams, _groupFunc, attributes);
        }
    }

    internal sealed class Split<TIn, TOut, TMat> : StageModule<TIn, TOut, TMat>
    {
        private readonly Func<TIn, SplitDecision> _splitDecision;

        public Split(Func<TIn, SplitDecision> splitDecision, Attributes attributes = null)
        {
            _splitDecision = splitDecision;
            Attributes = attributes ?? DefaultAttributes.Split;
        }

        public override IModule CarbonCopy()
        {
            return new Split<TIn, TOut, TMat>(_splitDecision, Attributes);
        }

        public override Attributes Attributes { get; }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new Split<TIn, TOut, TMat>(_splitDecision, attributes);
        }
    }

    internal static class Split
    {
        public static Split<TIn, TOut, TMat> When<TIn, TOut, TMat>(Predicate<TIn> predicate)
        {
            return new Split<TIn, TOut, TMat>(element => predicate(element) ? SplitDecision.SplitBefore : SplitDecision.Continue, Attributes.CreateName("splitWhen"));
        }

        public static Split<TIn, TOut, TMat> After<TIn, TOut, TMat>(Predicate<TIn> predicate)
        {
            return new Split<TIn, TOut, TMat>(element => predicate(element) ? SplitDecision.SplitAfter : SplitDecision.Continue, Attributes.CreateName("splitAfter"));
        }
    }

    internal sealed class DirectProcessor<TIn, TOut, TMat> : StageModule<TIn, TOut, TMat>
    {
        private readonly Func<Tuple<IProcessor<TIn, TOut>, TMat>> _factory;

        public DirectProcessor(Func<Tuple<IProcessor<TIn, TOut>, TMat>> factory, Attributes attributes = null)
        {
            _factory = factory;
            Attributes = attributes ?? DefaultAttributes.Processor;
        }

        public override IModule CarbonCopy()
        {
            return new DirectProcessor<TIn, TOut, TMat>(_factory, Attributes);
        }

        public override Attributes Attributes { get; }
        public override IModule WithAttributes(Attributes attributes)
        {
            return new DirectProcessor<TIn, TOut, TMat>(_factory, attributes);
        }
    }
}