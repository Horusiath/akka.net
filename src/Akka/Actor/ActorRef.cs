﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Dispatch.SysMsg;
using System.Threading;

namespace Akka.Actor
{
    
    /// <summary>
    /// All ActorRefs have a scope which describes where they live. Since it is often
    /// necessary to distinguish between local and non-local references, this is the only
    /// method provided on the scope.
    /// </summary>
// ReSharper disable once InconsistentNaming
    internal interface ActorRefScope
    {
        bool IsLocal { get; }
    }

    /// <summary>
    /// Marker interface for Actors that are deployed within local scope
    /// </summary>
// ReSharper disable once InconsistentNaming
    internal interface LocalRef : ActorRefScope { }

    public class FutureActorRef : MinimalActorRef
    {
        private readonly TaskCompletionSource<object> _result;
        private readonly Action _unregister;
        private ActorRef _sender;

        public FutureActorRef(TaskCompletionSource<object> result, ActorRef sender, Action unregister, ActorPath path)
        {
            _result = result;
            _sender = sender ?? ActorRef.NoSender;
            _unregister = unregister;
            Path = path;
        }

        public override ActorRefProvider Provider
        {
            get { throw new NotImplementedException(); }
        }

        
        private const int INITIATED = 0;
        private const int COMPLETED = 1;
        private int status = INITIATED;
        protected override void TellInternal(object message, ActorRef sender)
        {
            if (message is SystemMessage) //we have special handling for system messages
            {
                SendSystemMessage(message.AsInstanceOf<SystemMessage>(), sender);
            }
            else
            {
                if (Interlocked.Exchange(ref status,COMPLETED) == INITIATED)
                {
                    _unregister();
                    if (_sender == NoSender || message is Terminated)
                    {
                        _result.TrySetResult(message);
                    }
                    else
                    {
                        _sender.Tell(new CompleteFuture(() => _result.TrySetResult(message)));
                    }
                }
            }            
        }

        protected void SendSystemMessage(SystemMessage message, ActorRef sender)
        {
            PatternMatch.Match(message)
                .With<Terminate>(t => Stop())
                .With<DeathWatchNotification>(d => Tell(new Terminated(d.Actor, d.ExistenceConfirmed, d.AddressTerminated)));
        }
    }

    public class ActorRefSurrogate
    {
        public ActorRefSurrogate(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }
    }


    public abstract class ActorRef : ICanTell
    {

        public static readonly Nobody Nobody = new Nobody();
        public static readonly ReservedActorRef Reserved = new ReservedActorRef();
        public static readonly ActorRef NoSender = new NoSender();

        public virtual ActorPath Path { get; protected set; }

        public void Tell(object message, ActorRef sender)
        {
            if (sender == null)
            {
                throw new ArgumentNullException("sender");
            }

            TellInternal(message, sender);
        }

        public void Tell(object message)
        {
            ActorRef sender = null;

            if (ActorCell.Current != null)
            {
                sender = ActorCell.Current.Self;
            }
            else
            {
                sender = NoSender;
            }

            Tell(message, sender);
        }

        protected abstract void TellInternal(object message, ActorRef sender);

        public override string ToString()
        {
            return string.Format("[{0}]", Path);
        }

        public static implicit operator ActorRefSurrogate(ActorRef @ref)
        {
            if (@ref != null)
            {
                return new ActorRefSurrogate(Serialization.Serialization.SerializedActorPath(@ref));
            }

            return null;
        }

        public static implicit operator ActorRef(ActorRefSurrogate surrogate)
        {
            return Serialization.Serialization.CurrentSystem.Provider.ResolveActorRef(surrogate.Path);
        }
    }


    public abstract class InternalActorRef : ActorRef, ActorRefScope
    {
        public abstract InternalActorRef Parent { get; }
        public abstract ActorRefProvider Provider { get; }
        public abstract ActorRef GetChild(IEnumerable<string> name);
        public abstract void Resume(Exception causedByFailure = null);
        public abstract void Stop();
        public abstract void Restart(Exception cause);
        public abstract void Suspend();

        public bool IsTerminated { get; internal set; }
        public abstract bool IsLocal { get; }
    }

    public abstract class MinimalActorRef : InternalActorRef, LocalRef
    {
        public override InternalActorRef Parent
        {
            get { return Nobody; }
        }

        public override ActorRef GetChild(IEnumerable<string> name)
        {
            if (name.All(string.IsNullOrEmpty))
                return this;
            return Nobody;
        }

        public override void Resume(Exception causedByFailure = null)
        {
        }

        public override void Stop()
        {
        }

        public override void Restart(Exception cause)
        {
        }

        public override void Suspend()
        {
        }

        protected override void TellInternal(object message, ActorRef sender)
        {
        }

        public override bool IsLocal
        {
            get { return true; }
        }
    }

    public sealed class Nobody : MinimalActorRef
    {
        public override ActorRefProvider Provider
        {
            get { throw new NotSupportedException("Nobody does not provide"); }
        }
    }

    public sealed class ReservedActorRef : MinimalActorRef
    {
        public override ActorRefProvider Provider
        {
            get { throw new NotSupportedException("Reserved does not provide"); }
        }
    }

    public abstract class ActorRefWithCell : InternalActorRef
    {
        public ActorCell Cell { get; protected set; }

        public abstract IEnumerable<ActorRef> Children { get; }

        public abstract InternalActorRef GetSingleChild(string name);
    }

    public sealed class NoSender : ActorRef
    {
        public NoSender()
        {
            Path = null;
        }

        protected override void TellInternal(object message, ActorRef sender)
        {
        }
    }

    public class VirtualPathContainer : MinimalActorRef
    {
        private readonly InternalActorRef parent;
        private readonly ActorRefProvider provider;

        protected ConcurrentDictionary<string, InternalActorRef> children =
            new ConcurrentDictionary<string, InternalActorRef>();

        public VirtualPathContainer(ActorRefProvider provider, ActorPath path, InternalActorRef parent)
        {
            Path = path;
            this.parent = parent;
            this.provider = provider;
        }

        public override ActorRefProvider Provider
        {
            get { return provider; }
        }

        public override InternalActorRef Parent
        {
            get { return parent; }
        }

        private InternalActorRef GetChild(string name)
        {
            return children[name];
        }

        public void AddChild(string name, InternalActorRef actor)
        {
            children.AddOrUpdate(name, actor, (k, v) =>
            {
                //TODO:  log.debug("{} replacing child {} ({} -> {})", path, name, old, ref)
                return v;
            });
        }

        public void RemoveChild(string name)
        {
            InternalActorRef tmp;
            if (!children.TryRemove(name, out tmp))
            {
                //TODO: log.warning("{} trying to remove non-child {}", path, name)
            }
        }

        /*
override def getChild(name: Iterator[String]): InternalActorRef = {
    if (name.isEmpty) this
    else {
      val n = name.next()
      if (n.isEmpty) this
      else children.get(n) match {
        case null ⇒ Nobody
        case some ⇒
          if (name.isEmpty) some
          else some.getChild(name)
      }
    }
  }
*/

        public override ActorRef GetChild(IEnumerable<string> name)
        {
            //TODO: I have no clue what the scala version does
            if (!name.Any())
                return this;

            string n = name.First();
            if (string.IsNullOrEmpty(n))
                return this;
            InternalActorRef c = children[n];
            if (c == null)
                return Nobody;
            return c.GetChild(name.Skip(1));
        }

        public void ForeachActorRef(Action<ActorRef> action)
        {
            foreach (InternalActorRef child in children.Values)
            {
                action(child);
            }
        }
    }
}