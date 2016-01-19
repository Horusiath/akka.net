﻿using System;
using Akka.Actor;
using Akka.Event;
using Akka.Streams.Actors;

namespace Akka.Streams.Implementation
{
    public class ActorRefSourceActor : Actors.ActorPublisher<object>
    {
        public static Props Props(int bufferSize, OverflowStrategy overflowStrategy)
        {
            if (overflowStrategy == OverflowStrategy.Backpressure)
                throw new NotSupportedException("Backpressure overflow strategy not supported");

            return Actor.Props.Create(() => new ActorRefSourceActor(bufferSize, overflowStrategy));
        }

        protected readonly FixedSizeBuffer<object> Buffer;

        public readonly int BufferSize;
        public readonly OverflowStrategy OverflowStrategy;
        private ILoggingAdapter _log;

        public ActorRefSourceActor(int bufferSize, OverflowStrategy overflowStrategy)
        {
            BufferSize = bufferSize;
            OverflowStrategy = overflowStrategy;
            Buffer = bufferSize != 0 ? FixedSizeBuffer.Create<object>(bufferSize) : null;
        }

        protected ILoggingAdapter Log { get { return _log ?? (_log = Context.GetLogger()); } }

        protected override bool Receive(object message)
        {
            return DefaultReceive(message) || RequestElement(message) || ReceiveElement(message);
        }

        protected bool DefaultReceive(object message)
        {
            if (message is Cancel) Context.Stop(Self);
            else if (message is Status.Success)
            {
                if (BufferSize == 0 || Buffer.IsEmpty) Context.Stop(Self);  // will complete the stream successfully
                else Context.Become(DrainBufferThenComplete);
            }
            else if (message is Status.Failure && IsActive) OnErrorThenStop(((Status.Failure)message).Cause);
            else return false;
            return true;
        }

        protected virtual bool RequestElement(object message)
        {
            if (message is Request)
            {
                // totalDemand is tracked by base
                if (BufferSize != 0)
                    while (TotalDemand > 0L && !Buffer.IsEmpty)
                        OnNext(Buffer.Dequeue());

                return true;
            }

            return false;
        }

        protected virtual bool ReceiveElement(object message)
        {
            if (IsActive)
            {
                if (TotalDemand > 0L) OnNext(message);
                else if (BufferSize == 0) Log.Debug("Dropping element because there is no downstream demand: [{0}]", message);
                else if (!Buffer.IsFull) Buffer.Enqueue(message);
                else
                {
                    switch (OverflowStrategy)
                    {
                        case OverflowStrategy.DropHead:
                            Buffer.DropHead();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropTail:
                            Buffer.DropTail();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropBuffer:
                            Buffer.Clear();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropNew:
                            // do not enqueue new element if the buffer is full
                            break;
                        case OverflowStrategy.Fail:
                            OnErrorThenStop(new BufferOverflowException(string.Format("Buffer overflow, max capacity was ({0})", BufferSize)));
                            break;
                        case OverflowStrategy.Backpressure:
                            // there is a precondition check in Source.actorRefSource factory method
                            break;
                    }
                }

                return true;
            }

            return false;
        }

        private bool DrainBufferThenComplete(object message)
        {
            if (message is Cancel) Context.Stop(Self);
            else if (message is Status.Failure && IsActive)
            {
                // errors must be signalled as soon as possible,
                // even if previously valid completion was requested via Status.Success
                OnErrorThenStop(((Status.Failure)message).Cause);
            }
            else if (message is Request)
            {
                // totalDemand is tracked by base
                while (TotalDemand > 0L && !Buffer.IsEmpty)
                    OnNext(Buffer.Dequeue());

                if (Buffer.IsEmpty) Context.Stop(Self); // will complete the stream successfully
            }
            else if (IsActive)
            {
                Log.Debug("Dropping element because Status.Success received already, only draining already buffered elements: [{0}] (pending: [{1}])", message, Buffer.Used);
            }
            else return false;
            return true;
        }
    }
}