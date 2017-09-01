//-----------------------------------------------------------------------
// <copyright file="TlsActor.cs" company="Akka.NET Project">
//     Copyright (C) 2015-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
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

        

        #endregion

        public static Actor.Props Props(TlsSettings settings, 
            Func<ActorSystem, SslStream> sslStreamFactory,
            bool tracing = false) =>
            Actor.Props.Create(() => new TlsActor(settings, sslStreamFactory, tracing)).WithDeploy(Deploy.Local);

        private readonly TlsSettings _settings;
        private SslStream _sslStream;
        private readonly bool _tracing;
        private TlsRole _role;
        private ILoggingAdapter _log;

        private TlsActor(TlsSettings settings, Func<ActorSystem, SslStream> sslStreamFactory, bool tracing)
        {
            _settings = settings;
            _tracing = tracing;
            _sslStream = sslStreamFactory(Context.System);
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