//-----------------------------------------------------------------------
// <copyright file="Tls.cs" company="Akka.NET Project">
//     Copyright (C) 2015-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Immutable;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Akka.IO;

namespace Akka.Streams.Dsl
{

    public abstract class TlsSettings
    {
        public static TlsClientSettings Client(string targetHost,
            SslProtocols enabledProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
            bool checkCertificateRevocation = false,
            params X509Certificate2[] certificates) => new TlsClientSettings(targetHost, enabledProtocols, checkCertificateRevocation, certificates);

        public static TlsServerSettings Server(X509Certificate2 certificate,
            bool negotiateClientCertificate = false,
            SslProtocols enabledProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
            bool checkCertificateRevocation = false) => new TlsServerSettings(certificate, negotiateClientCertificate, enabledProtocols, checkCertificateRevocation);

        public SslProtocols EnabledProtocols { get; }
        public bool CheckCertificateRevocation { get; }

        protected TlsSettings(SslProtocols enabledProtocols, bool checkCertificateRevocation)
        {
            EnabledProtocols = enabledProtocols;
            CheckCertificateRevocation = checkCertificateRevocation;
        }
    }

    public sealed class TlsServerSettings : TlsSettings
    {
        public X509Certificate2 Certificate { get; }
        public bool NegotiateClientCertificate { get; }

        public TlsServerSettings(X509Certificate2 certificate, 
            bool negotiateClientCertificate = false, 
            SslProtocols enabledProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12, 
            bool checkCertificateRevocation = false)
            : base(enabledProtocols, checkCertificateRevocation)
        {
            Certificate = certificate;
            NegotiateClientCertificate = negotiateClientCertificate;
        }
    }

    public sealed class TlsClientSettings : TlsSettings
    {
        public string TargetHost { get; }
        public ImmutableArray<X509Certificate2> Certificates { get; }

        public TlsClientSettings(string targetHost, 
            SslProtocols enabledProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12, 
            bool checkCertificateRevocation = false, 
            params X509Certificate2[] certificates)
            : base(enabledProtocols, checkCertificateRevocation)
        {
            TargetHost = targetHost;
            Certificates = certificates?.ToImmutableArray() ?? ImmutableArray<X509Certificate2>.Empty;
        }
    }

    /// <summary>
    /// Stream cipher support.
    /// 
    /// The underlying SSLEngine has four ports: plaintext input/output and
    /// ciphertext input/output. These are modeled as a [[akka.stream.BidiShape]]
    /// element for use in stream topologies, where the plaintext ports are on the
    /// left hand side of the shape and the ciphertext ports on the right hand side.
    /// 
    /// Configuring JSSE is a rather complex topic, please refer to the JDK platform
    /// documentation or the excellent user guide that is part of the Play Framework
    /// documentation. The philosophy of this integration into Akka Streams is to
    /// expose all knobs and dials to client code and therefore not limit the
    /// configuration possibilities. In particular the client code will have to
    /// provide the SSLContext from which the SSLEngine is then created. Handshake
    /// parameters are set using <see cref="NegotiateNewSession"/> messages, the settings for
    /// the initial handshake need to be provided up front using the same class;
    /// please refer to the method documentation below.
    /// </summary>
    /// <remarks>
    /// The TLS specification does not permit half-closing of the user data session
    /// that it transports—to be precise a half-close will always promptly lead to a
    /// full close. This means that canceling the plaintext output or completing the
    /// plaintext input of the SslTls stage will lead to full termination of the
    /// secure connection without regard to whether bytes are remaining to be sent or
    /// received, respectively. Especially for a client the common idiom of attaching
    /// a finite Source to the plaintext input and transforming the plaintext response
    /// bytes coming out will not work out of the box due to early termination of the
    /// connection. For this reason there is a parameter that determines whether the
    /// SslTls stage shall ignore completion and/or cancellation events, and the
    /// default is to ignore completion (in view of the client–server scenario). In
    /// order to terminate the connection the client will then need to cancel the
    /// plaintext output as soon as all expected bytes have been received. When
    /// ignoring both types of events the stage will shut down once both events have
    /// been received.
    /// </remarks>
    public static class Tls
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        public static BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> Client(TlsClientSettings settings)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        public static BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> Server(TlsServerSettings settings)
        {
            throw new NotImplementedException();
        }
    }
}