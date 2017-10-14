//-----------------------------------------------------------------------
// <copyright file="TlsActor.cs" company="Akka.NET Project">
//     Copyright (C) 2015-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.IO;
using System.Net.Security;
using Akka.Actor;
using Akka.Event;
using Akka.IO;
using Akka.Streams.Dsl;

namespace Akka.Streams.Implementation.IO
{
    internal sealed class TlsActor : ActorBase, IPump
    {
        #region internal classes

        private sealed class AdapterStream : Stream
        {
            private readonly TlsActor _owner;

            public AdapterStream(TlsActor owner)
            {
                _owner = owner;
            }

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
        }

        private sealed class TlsInputBunch : InputBunch
        {
            private readonly TlsActor _actor;

            public TlsInputBunch(int inputCount, int bufferSize, TlsActor actor) : base(inputCount, bufferSize, actor)
            {
                _actor = actor;
            }

            public override void OnError(int id, Exception cause)
            {
                _actor.Fail(cause);
            }
        }

        /**
         * The SSLEngine needs bite-sized chunks of data but we get arbitrary ByteString
         * from both the UserIn and the TransportIn ports. This is used to chop up such
         * a ByteString by filling the respective ByteBuffer and taking care to dequeue
         * a new element when data are demanded and none are left lying on the chopping
         * block.
         */
        private sealed class ChoppingBlock : TransferState
        {
            private readonly TlsActor _actor;
            private readonly int _index;
            private readonly string _name;

            private ByteString _buffer = ByteString.Empty;

            public ChoppingBlock(TlsActor actor, int index, string name)
            {
                _actor = actor;
                _index = index;
                _name = name;
            }

            public bool IsEmpty => _buffer.IsEmpty;
            private InputBunch InputBunch => _actor._inputBunch;
            private OutputBunch<ByteString> OutputBunch => _actor._outputBunch;

            public override bool IsReady => !_buffer.IsEmpty 
                || InputBunch.IsPending(_index) 
                || InputBunch.IsDepleted(_index);

            public override bool IsCompleted => InputBunch.IsCancelled(_index);

            /// <summary>
            /// Pour as many bytes as are available either on the chopping block or in
            /// the inputBunch’s next ByteString into the supplied ByteBuffer, which is
            /// expected to be in “read left-overs” mode, i.e. everything between its
            /// position and limit is retained. In order to allocate a fresh ByteBuffer
            /// with these characteristics, use `prepare()`.
            /// </summary>
            public void ChopInto(ArraySegment<byte> b)
            {
                if (_buffer.IsEmpty)
                {
                    switch (InputBunch.Dequeue(_index))
                    {
                        case ByteString bs: _buffer = bs; break;
                        case SendBytes sb: _buffer = sb.Payload; break;
                        case NegotiateNewSession n: 
                            // setNewSessionParameters(n)
                            _buffer = ByteString.Empty;
                            break;
                    }

                    if (_actor._tracing) _actor.Log.Debug("chopping from new chunk of {0} into {1} ({2})", _buffer.Count, _name, b.Count);
                }
                else if (_actor._tracing) _actor.Log.Debug("chopping from old chunk of {0} into {1} ({2})", _buffer.Count, _name, b.Count);

                var copied = _buffer.CopyTo(b.Array, b.Offset, b.Count);
                _buffer = _buffer.Slice(copied);
            }

            /// <summary>
            /// When potentially complete packet data are left after unwrap() we must
            /// put them back onto the chopping block because otherwise the pump will
            /// not know that we are runnable.
            /// </summary>
            public void PutBack(ArraySegment<byte> b)
            {
                if (b.Count > 0)
                {
                    if (_actor._tracing) _actor.Log.Debug("putting back {0} bytes into {1}", b.Count, _name);
                    var bs = ByteString.CopyFrom(b);
                    _buffer = bs + _buffer;
                }
            }
        }

        #endregion

        public const int TransportIn = 0;
        public const int TransportOut = 0;

        public const int UserIn = 1;
        public const int UserOut = 1;

        public static Actor.Props Props(ActorMaterializerSettings settings, 
            Func<Stream, SslStream> sslStreamFactory,
            bool tracing = false) =>
            Actor.Props.Create(() => new TlsActor(settings, sslStreamFactory, tracing)).WithDeploy(Deploy.Local);
        
        private readonly OutputBunch<ByteString> _outputBunch;
        private readonly InputBunch _inputBunch;

        private readonly ActorMaterializerSettings _materializerSettings;
        private SslStream _sslStream;
        private AdapterStream _adapterStream;
        private readonly bool _tracing;
        private TlsRole _role;
        private ILoggingAdapter _log;
        
        // These are Netty's default values
        // 16665 + 1024 (room for compressed data) + 1024 (for OpenJDK compatibility)
        private readonly byte[] _transportOutBuffer = new byte[16665 + 2048];

        /*
         * deviating here: chopping multiple input packets into this buffer can lead to
         * an OVERFLOW signal that also is an UNDERFLOW; avoid unnecessary copying by
         * increasing this buffer size to host up to two packets
         */
        private readonly byte[] _userOutBuffer = new byte[16665 * 2 + 2048];
        private readonly byte[] _transportInBuffer = new byte[16665 + 2048];
        private readonly byte[] _userInBuffer = new byte[16665 + 2048];

        private readonly ChoppingBlock _userInChoppingBlock;
        private readonly ChoppingBlock _transportInChoppingBlock;

        private readonly TransferState _needsWrap;
        private readonly TransferState _inboundOpen;
        private readonly TransferState _userHasData;
        private readonly TransferState _userOutCancelled;

        // bidirectional case
        private readonly TransferState _inbound;
        private readonly TransferState _outbound;

        // half-closed
        private readonly TransferState _outboundHalfClosed;
        private readonly TransferState _inboundHalfClosed;

        private TlsActor(ActorMaterializerSettings materializerSettings, Func<Stream, SslStream> sslStreamFactory, bool tracing)
        {
            _materializerSettings = materializerSettings;
            _tracing = tracing;
            _adapterStream = new AdapterStream(this);
            _sslStream = sslStreamFactory(_adapterStream);
            //_role = settings is TlsServerSettings ? TlsRole.Server : TlsRole.Client;

            _outputBunch = new OutputBunch<ByteString>(2, Self, this);
            _inputBunch = new TlsInputBunch(2, materializerSettings.MaxInputBufferSize, this);

            _userInChoppingBlock = new ChoppingBlock(this, UserIn, "UserIn");
            _transportInChoppingBlock = new ChoppingBlock(this, TransportIn, "TransportIn");

            _inbound = _userHasData.Or(_needsWrap).And(_outputBunch.DemandAvailableFor(TransportOut));
            _outbound = _transportInChoppingBlock.And(_outputBunch.DemandAvailableFor(UserOut)).Or(_userOutCancelled);

            _outboundHalfClosed = _needsWrap.And(_outputBunch.DemandAvailableFor(TransportOut));
            _inboundHalfClosed = _transportInChoppingBlock.And(_inboundOpen);

            _sslStream.AuthenticateAsServerAsync();

            this.Init();
        }

        public bool IsServer => _role == TlsRole.Server;
        public bool IsClient => _role == TlsRole.Client;
        public ILoggingAdapter Log => _log ?? (_log = Context.GetLogger());

        protected override bool Receive(object message) => 
            _inputBunch.SubReceive.CurrentReceive(message) || _outputBunch.SubReceive.CurrentReceive(message);

        protected override void PostStop()
        {
            _sslStream?.Dispose();
            base.PostStop();
        }

        private void SetNewSession(NegotiateNewSession negotiate)
        {
            if (_tracing) Log.Debug("Applying {0}", negotiate);
            throw new NotImplementedException();
        }

        private ByteString Decode(ByteString data)
        {
            throw new NotImplementedException();
        }

        private ByteString Encode(ByteString data)
        {
            throw new NotImplementedException();
        }

        private void Fail(Exception cause, bool closeTransport = true)
        {
            if (_tracing) Log.Debug("Fail {0} due to: {1}", Self, cause);
            _inputBunch.Cancel();
            if (closeTransport)
            {
                Log.Debug("Closing output");
                _outputBunch.Error(TransportOut, cause);
            }

            _outputBunch.Error(UserOut, cause);
            Pump();
        }

        #region IPump impl

        public TransferState TransferState { get; set; }
        public Action CurrentAction { get; set; }
        public bool IsPumpFinished => this.IsPumpFinished();
        public void InitialPhase(int waitForUpstream, TransferPhase andThen) => Pumps.InitialPhase(this, waitForUpstream, andThen);
        public void WaitForUpstream(int waitForUpstream) => Pumps.WaitForUpstream(this, waitForUpstream);
        public void GotUpstreamSubscription() => Pumps.GotUpstreamSubscription(this);
        public void NextPhase(TransferPhase phase) => Pumps.NextPhase(this, phase);
        public void Pump() => Pumps.Pump(this);
        public void PumpFailed(Exception e) => Fail(e);
        public void PumpFinished()
        {
            _inputBunch.Cancel();
            _outputBunch.Complete();
            if (_tracing) Log.Debug("STOP Outbound Closed: {0} Inbound Closed: {1}", _sslStream.CanWrite, _sslStream.CanRead);
            Context.Stop(Self);
        }

        #endregion
    }
}