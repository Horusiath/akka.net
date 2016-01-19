﻿using System;
using System.Collections.Generic;
using Akka.Pattern;
using Akka.Streams.Supervision;

namespace Akka.Streams.Stage
{
    /// <summary>
    /// General interface for stream transformation.
    /// 
    /// Custom <see cref="IStage{TIn, TOut}"/> implementations are intended to be used with
    /// [[akka.stream.scaladsl.FlowOps#transform]] or
    /// [[akka.stream.javadsl.Flow#transform]] to extend the `Flow` API when there
    /// is no specialized operator that performs the transformation.
    /// 
    /// Custom implementations are subclasses of <see cref="PushPullStage{TIn, TOut}"/> or
    /// <see cref="DetachedStage{TIn, TOut}"/>. Sometimes it is convenient to extend
    /// <see cref="StatefulStage{TIn, TOut}"/> for support of become like behavior.
    /// 
    /// It is possible to keep state in the concrete <see cref="IStage{TIn, TOut}"/> instance with
    /// ordinary instance variables. The <see cref="ITransformerLike{TIn,TOut}"/> is executed by an actor and
    /// therefore you do not have to add any additional thread safety or memory
    /// visibility constructs to access the state from the callback methods.
    /// </summary>
    public interface IStage<in TIn, out TOut> { }
    
    /// <summary>
    /// <para>
    /// <see cref="PushPullStage{TIn,TOut}"/> implementations participate in 1-bounded regions. For every external non-completion signal these
    /// stages produce *exactly one* push or pull signal.
    /// </para>
    /// <para>
    /// <see cref="OnPush"/> is called when an element from upstream is available and there is demand from downstream, i.e.
    /// in <see cref="OnPush"/> you are allowed to call <see cref="IContext.Push"/> to emit one element downstreams, or you can absorb the
    /// element by calling <see cref="IContext.Pull"/>. Note that you can only emit zero or one element downstream from <see cref="OnPull"/>.
    /// To emit more than one element you have to push the remaining elements from <see cref="OnPush"/>, one-by-one.
    /// <see cref="OnPush"/> is not called again until <see cref="OnPull"/> has requested more elements with <see cref="IContext.Pull"/>.
    /// </para>
    /// <para>
    /// <see cref="StatefulStage{TIn,TOut}"/> has support for making it easy to emit more than one element from <see cref="OnPush"/>.
    /// </para>
    /// <para>
    /// <see cref="OnPull"/> is called when there is demand from downstream, i.e. you are allowed to push one element
    /// downstreams with <see cref="IContext.Push"/>, or request elements from upstreams with <see cref="IContext.Pull"/>. If you
    /// always perform transitive pull by calling <see cref="IContext.Pull"/> from <see cref="OnPull"/> you can use 
    /// <see cref="PushStage{TIn,TOut}"/> instead of <see cref="PushPullStage{TIn,TOut}"/>.
    /// </para>
    /// <para>
    /// Stages are allowed to do early completion of downstream and cancel of upstream. This is done with <see cref="IContext.Finish"/>,
    /// which is a combination of cancel/complete.
    /// </para>
    /// <para>
    /// Since OnComplete is not a backpressured signal it is sometimes preferable to push a final element and then
    /// immediately finish. This combination is exposed as <see cref="IContext.PushAndFinish"/> which enables stages to
    /// propagate completion events without waiting for an extra round of pull.
    /// </para>
    /// <para>
    /// Another peculiarity is how to convert termination events (complete/failure) into elements. The problem
    /// here is that the termination events are not backpressured while elements are. This means that simply calling
    /// <see cref="IContext.Push"/> as a response to <see cref="OnUpstreamFinish"/> or <see cref="OnUpstreamFailure"/> will very likely break boundedness
    /// and result in a buffer overflow somewhere. Therefore the only allowed command in this case is
    /// <see cref="IContext.AbsorbTermination"/> which stops the propagation of the termination signal, and puts the stage in a
    /// <see cref="IContext.IsFinishing"/> state. Depending on whether the stage has a pending pull signal it
    /// has not yet "consumed" by a push its <see cref="OnPull"/> handler might be called immediately or later. From
    /// <see cref="OnPull"/> final elements can be pushed before completing downstream with <see cref="IContext.Finish"/> or
    /// <see cref="IContext.PushAndFinish"/>.
    /// </para>
    /// <para>
    /// <see cref="StatefulStage{TIn,TOut}"/> has support for making it easy to emit final elements.
    /// </para>
    /// <para>
    /// All these rules are enforced by types and runtime checks where needed. Always return the <see cref="Directive"/>
    /// from the call to the <see cref="IContext"/> method, and do only call <see cref="IContext"/> commands once per callback.
    /// </para>
    /// </summary>
    /// <seealso cref="DetachedStage{TIn,TOut}"/>
    /// <seealso cref="StatefulStage{TIn,TOut}"/>
    /// <seealso cref="PushStage{TIn,TOut}"/>
    public abstract class PushPullStage<TIn, TOut> : AbstractStage<TIn, TOut, ISyncDirective, ISyncDirective, IContext<TOut>, ILifecycleContext> { }

    /// <summary>
    /// <see cref="PushStage{TIn,TOut}"/> is a <see cref="PushPullStage{TIn,TOut}"/> that always perform transitive pull by calling <see cref="IContext.Pull"/> from <see cref="OnPull"/>.
    /// </summary>
    public abstract class PushStage<TIn, TOut> : PushPullStage<TIn, TOut>
    {
        /// <summary>
        /// Always pulls from upstream.
        /// </summary>
        public sealed override ISyncDirective OnPull(IContext<TOut> context)
        {
            return context.Pull();
        }
    }

    /// <summary>
    /// `DetachedStage` can be used to implement operations similar to [[akka.stream.scaladsl.FlowOps#buffer buffer]],
    /// [[akka.stream.scaladsl.FlowOps#expand expand]] and [[akka.stream.scaladsl.FlowOps#conflate conflate]].
    /// 
    /// `DetachedStage` implementations are boundaries between 1-bounded regions. This means that they need to enforce the
    /// "exactly one" property both on their upstream and downstream regions. As a consequence a `DetachedStage` can never
    /// answer an [[#onPull]] with a [[Context#pull]] or answer an [[#onPush]] with a [[Context#push]] since such an action
    /// would "steal" the event from one region (resulting in zero signals) and would inject it to the other region
    /// (resulting in two signals).
    /// 
    /// However, DetachedStages have the ability to call [[akka.stream.stage.DetachedContext#hold]] as a response to
    /// [[#onPush]] and [[#onPull]] which temporarily takes the signal off and
    /// stops execution, at the same time putting the stage in an [[akka.stream.stage.DetachedContext#isHolding]] state.
    /// If the stage is in a holding state it contains one absorbed signal, therefore in this state the only possible
    /// command to call is [[akka.stream.stage.DetachedContext#pushAndPull]] which results in two events making the
    /// balance right again: 1 hold + 1 external event = 2 external event
    /// 
    /// This mechanism allows synchronization between the upstream and downstream regions which otherwise can progress
    /// independently.
    /// 
    /// @see [[PushPullStage]]
    /// </summary>
    public abstract class DetachedStage<TIn, TOut> : AbstractStage<TIn, TOut, IUpstreamDirective, IDownstreamDirective, IDetachedContext<TOut>, ILifecycleContext>
    {
        protected override bool IsDetached
        {
            get { return true; }
        }

        /**
         * If an exception is thrown from [[#onPush]] this method is invoked to decide how
         * to handle the exception. By default this method returns [[Supervision.Stop]].
         *
         * If an exception is thrown from [[#onPull]] or if the stage is holding state the stream
         * will always be completed with failure, because it is not always possible to recover from
         * that state.
         * In concrete stages it is of course possible to use ordinary try-catch-recover inside
         * `onPull` when it is know how to recover from such exceptions.
         */
        public override Supervision.Directive Decide(Exception cause)
        {
            return base.Decide(cause);
        }
    }

    /// <summary>
    /// The behavior of <see cref="StatefulStage{TIn,TOut}"/> is defined by these two methods, which
    /// has the same semantics as corresponding methods in <see cref="PushPullStage{TIn,TOut}"/>.
    /// </summary>
    public abstract class StageState<TIn, TOut>
    {
        public abstract ISyncDirective OnPush(TIn element, IContext<TOut> context);

        public virtual ISyncDirective OnPull(IContext<TOut> context)
        {
            return context.Pull();
        }
    }

    public static class StatefulStage
    {
        #region Internal API

        internal interface IAndThen { }

        [Serializable]
        internal sealed class Finish : IAndThen
        {
            public static readonly Finish Instance = new Finish();
            private Finish() { }
        }

        [Serializable]
        internal sealed class Become<TIn, TOut> : IAndThen
        {
            public readonly StageState<TIn, TOut> State;

            public Become(StageState<TIn, TOut> state)
            {
                State = state;
            }
        }

        [Serializable]
        internal sealed class Stay : IAndThen
        {
            public static readonly Stay Instance = new Stay();
            private Stay() { }
        }

        #endregion
    }

    /// <summary>
    /// <see cref="StatefulStage{TIn,TOut}"/> is a <see cref="PushPullStage{TIn,TOut}"/> that provides convenience to make some things easier.
    /// 
    /// The behavior is defined in <see cref="StageState{TIn,TOut}"/> instances. The initial behavior is specified
    /// by subclass implementing the <see cref="Initial"/> method. The behavior can be changed by using <see cref="Become"/>.
    /// 
    /// Use <see cref="Emit"/> or <see cref="EmitAndFinish"/> to push more than one element from <see cref="StageState{TIn,TOut}.OnPush"/> or
    /// <see cref="StageState{TIn,TOut}.OnPull"/>.
    /// 
    /// Use <see cref="TerminationEmit"/> to push final elements from <see cref="OnUpstreamFinish"/> or <see cref="OnUpstreamFailure"/>.
    /// </summary>
    public abstract class StatefulStage<TIn, TOut> : PushPullStage<TIn, TOut>
    {
        private bool _isEmitting = false;
        private StageState<TIn, TOut> _current;

        protected StatefulStage(StageState<TIn, TOut> current)
        {
            _current = current;
            Become(Initial);
        }

        /**
         * Concrete subclass must return the initial behavior from this method.
         *
         * **Warning:** This method must not be implemented as `val`.
         */
        public abstract StageState<TIn, TOut> Initial { get; }

        /// <summary>
        /// Current state.
        /// </summary>
        public StageState<TIn, TOut> Current { get { return _current; } }

        /// <summary>
        /// Change the behavior to another <see cref="StageState{TIn,TOut}"/>.
        /// </summary>
        public void Become(StageState<TIn, TOut> state)
        {
            if (state == null) throw new ArgumentNullException("state");
            _current = state;
        }

        /// <summary>
        /// Invokes current state.
        /// </summary>
        public sealed override ISyncDirective OnPush(TIn element, IContext<TOut> context)
        {
            return _current.OnPush(element, context);
        }

        /// <summary>
        /// Invokes current state.
        /// </summary>
        public sealed override ISyncDirective OnPull(IContext<TOut> context)
        {
            return _current.OnPull(context);
        }

        public override ITerminationDirective OnUpstreamFinish(IContext<TOut> context)
        {
            return _isEmitting
                ? context.AbsorbTermination()
                : context.Finish();
        }

        /// <summary>
        /// Can be used from <see cref="StageState{TIn,TOut}.OnPush"/> or <see cref="StageState{TIn,TOut}.OnPull"/> to push more than one
        /// element downstreams.
        /// </summary>
        public ISyncDirective Emit(IEnumerator<TOut> enumerator, IContext<TOut> context)
        {
            return Emit(enumerator, context, _current);
        }

        /// <summary>
        /// Can be used from <see cref="StageState{TIn,TOut}.OnPush"/> or <see cref="StageState{TIn,TOut}.OnPull"/> to push more than one
        /// element downstreams and after that change behavior.
        /// </summary>
        public ISyncDirective Emit(IEnumerator<TOut> enumerator, IContext<TOut> context, StageState<TIn, TOut> nextState)
        {
            if (_isEmitting) throw new IllegalStateException("Already in emitting state");
            if (!enumerator.MoveNext())
            {
                Become(nextState);
                return context.Pull();
            }
            else
            {
                var element = enumerator.Current;
                if (enumerator.MoveNext())
                {
                    _isEmitting = true;
                    Become(EmittingState(enumerator, new StatefulStage.Become<TIn, TOut>(nextState)));
                }
                else
                {
                    Become(nextState);
                }

                return context.Push(element);
            }
        }

        /// <summary>
        /// Can be used from <see cref="OnUpstreamFinish"/> to push final elements downstreams
        /// before completing the stream successfully. Note that if this is used from
        /// <see cref="OnUpstreamFailure"/> the failure will be absorbed and the stream will be completed
        /// successfully.
        /// </summary>
        public ISyncDirective TerminationEmit(IEnumerator<TOut> enumerator, IContext<TOut> context)
        {
            if (!enumerator.MoveNext())
            {
                return _isEmitting ? context.AbsorbTermination() : context.Finish();
            }
            else
            {
                var es = Current as EmittingState<TIn, TOut>;
                var nextState = es != null && _isEmitting
                    ? es.Copy(enumerator)
                    : EmittingState(enumerator, StatefulStage.Finish.Instance);
                Become(nextState);
                return context.AbsorbTermination();
            }
        }

        /// <summary>
        /// Can be used from <see cref="StageState{TIn,TOut}.OnPush"/> or <see cref="StageState{TIn,TOut}.OnPull"/> to push more than one
        /// element downstreams and after that finish (complete downstreams, cancel upstreams).
        /// </summary>
        public ISyncDirective EmitAndFinish(IEnumerator<TOut> enumerator, IContext<TOut> context)
        {
            if(_isEmitting) throw new IllegalStateException("Already emitting a state");
            if (!enumerator.MoveNext())
            {
                return context.Finish();
            }
            else
            {
                var elem = enumerator.Current;
                if (enumerator.MoveNext())
                {
                    _isEmitting = true;
                    Become(EmittingState(enumerator, StatefulStage.Finish.Instance));
                    return context.Push(elem);
                }
                else
                {
                    return context.PushAndFinish(elem);
                }
            }
        }

        private StageState<TIn, TOut> EmittingState(IEnumerator<TOut> enumerator, StatefulStage.IAndThen andThen)
        {
            return new EmittingState<TIn, TOut>(enumerator, andThen, context =>
            {
                if (enumerator.MoveNext())
                {
                    var element = enumerator.Current;
                    if (enumerator.MoveNext())
                    {
                        return context.Push(element);
                    }
                    else if (!context.IsFinishing)
                    {
                        _isEmitting = false;

                        if (andThen is StatefulStage.Stay) ;
                        else if (andThen is StatefulStage.Become<TIn, TOut>)
                        {
                            var become = andThen as StatefulStage.Become<TIn, TOut>;
                            Become(become.State);
                        }
                        else if (andThen is StatefulStage.Finish)
                        {
                            context.PushAndFinish(element);
                        }

                        return context.Push(element);
                    }
                    else return context.PushAndFinish(element);
                }
                else
                {
                    throw new IllegalStateException("OnPull with empty enumerator is not expected in emitting state");
                }
            });
        }
    }

    internal sealed class EmittingState<TIn, TOut> : StageState<TIn, TOut>
    {
        private readonly IEnumerator<TOut> _enumerator;
        private readonly Func<IContext<TOut>, ISyncDirective> _onPull;
        private readonly StatefulStage.IAndThen _andThen;

        public EmittingState(IEnumerator<TOut> enumerator, StatefulStage.IAndThen andThen, Func<IContext<TOut>, ISyncDirective> onPull)
        {
            _enumerator = enumerator;
            _onPull = onPull;
            _andThen = andThen;
        }

        public override ISyncDirective OnPull(IContext<TOut> context)
        {
            throw new NotImplementedException();
        }

        public override ISyncDirective OnPush(TIn element, IContext<TOut> context)
        {
            throw new IllegalStateException("OnPush is not allowed in emitting state");
        }

        public StageState<TIn, TOut> Copy(IEnumerator<TOut> enumerator)
        {
            throw new NotImplementedException();
        }
    }
}