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
    internal sealed class TlsActor : ActorBase
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

        #endregion

        public static Actor.Props Props(TlsSettings settings, 
            Func<Stream, SslStream> sslStreamFactory,
            bool tracing = false) =>
            Actor.Props.Create(() => new TlsActor(settings, sslStreamFactory, tracing)).WithDeploy(Deploy.Local);

        private readonly TlsSettings _settings;
        private SslStream _sslStream;
        private AdapterStream _adapterStream;
        private readonly bool _tracing;
        private TlsRole _role;
        private ILoggingAdapter _log;

        private TlsActor(TlsSettings settings, Func<Stream, SslStream> sslStreamFactory, bool tracing)
        {
            _settings = settings;
            _tracing = tracing;
            _adapterStream = new AdapterStream(this);
            _sslStream = sslStreamFactory(_adapterStream);
            _role = settings is TlsServerSettings ? TlsRole.Server : TlsRole.Client;
        }

        public bool IsServer => _role == TlsRole.Server;
        public bool IsClient => _role == TlsRole.Client;
        public ILoggingAdapter Log => _log ?? (_log = Context.GetLogger());

        protected override bool Receive(object message)
        {
            throw new NotImplementedException();
        }

        protected override void PostStop()
        {
            _sslStream?.Dispose();
            base.PostStop();
        }

        private bool Authenticating(object message) => throw new NotImplementedException();
        private bool Authenticated(object message) => throw new NotImplementedException();
        private bool AuthFailed(object message) => throw new NotImplementedException();
        private bool ReadBeforeAuth(object message) => throw new NotImplementedException();
        private bool FlushBeforeHandshake(object message) => throw new NotImplementedException();

        private ByteString Decode(ByteString data)
        {
            throw new NotImplementedException();
        }

        private ByteString Encode(ByteString data)
        {
            throw new NotImplementedException();
        }
    }
}