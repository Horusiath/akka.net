//-----------------------------------------------------------------------
// <copyright file="TlsOptions.cs" company="Akka.NET Project">
//     Copyright (C) 2015-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Net.Security;
using Akka.IO;

namespace Akka.Streams
{
    /// <summary>
    /// Many protocols are asymmetric and distinguish between the client and the
    /// server, where the latter listens passively for messages and the former
    /// actively initiates the exchange.
    /// </summary>
    public enum TlsRole
    {
        /// <summary>
        /// The client is usually the side that consumes the service provided by its
        /// interlocutor. The precise interpretation of this role is protocol specific.
        /// </summary>
        Client,

        /// <summary>
        /// The server is usually the side the provides the service to its interlocutor.
        /// The precise interpretation of this role is protocol specific.
        /// </summary>
        Server
    }

    /// <summary>
    /// All streams in Akka are unidirectional: while in a complex flow graph data
    /// may flow in multiple directions these individual flows are independent from
    /// each other. The difference between two half-duplex connections in opposite
    /// directions and a full-duplex connection is that the underlying transport
    /// is shared in the latter and tearing it down will end the data transfer in
    /// both directions.
    /// 
    /// When integrating a full-duplex transport medium that does not support
    /// half-closing (which means ending one direction of data transfer without
    /// ending the other) into a stream topology, there can be unexpected effects.
    /// Feeding a finite Source into this medium will close the connection after
    /// all elements have been sent, which means that possible replies may not
    /// be received in full. To support this type of usage, the sending and
    /// receiving of data on the same side (e.g. on the client) need to be
    /// coordinated such that it is known when all replies have been received.
    /// Only then should the transport be shut down.
    /// 
    /// To support these scenarios it is recommended that the full-duplex
    /// transport integration is configurable in terms of termination handling,
    /// which means that the user can optionally suppress the normal (closing)
    /// reaction to completion or cancellation events, as is expressed by the
    /// possible values of this type.
    /// </summary>
    [Flags]
    public enum TlsClosing
    {
        /// <summary>
        /// Means to not ignore signals.
        /// </summary>
        EagerClose = 0,

        /// <summary>
        /// Means to not react to cancellation of the receiving side unless the 
        /// sending side has already completed.
        /// </summary>
        IgnoreCancel   = 1,

        /// <summary>
        /// Means to not react to the completion of the sending side unless 
        /// the receiving side has already canceled.
        /// </summary>
        IgnoreComplete = 2,

        /// <summary>
        /// Means to ignore the first termination signal—be that cancellation 
        /// or completion—and only act upon the second one.
        /// </summary>
        IgnoreBoth     = IgnoreCancel&IgnoreComplete,
    }

    /// <summary>
    /// This is the supertype of all messages that the SslTls stage emits on the
    /// plaintext side.
    /// </summary>
    public interface ITlsInbound { }

    /// <summary>
    /// If the underlying transport is closed before the final TLS closure command
    /// is received from the peer then the SSLEngine will throw an SSLException that
    /// warns about possible truncation attacks. This exception is caught and
    /// translated into this message when encountered. Most of the time this occurs
    /// not because of a malicious attacker but due to a connection abort or a
    /// misbehaving communication peer.
    /// </summary>
    public sealed class SessionTruncated : ITlsInbound
    {
        public static readonly SessionTruncated Instance = new SessionTruncated();
        private SessionTruncated() { }
    }

    ///<summary>
    /// Plaintext bytes emitted by the SSLEngine are received over one specific
    /// encryption session and this class bundles the bytes with the SSLSession
    /// object. When the session changes due to renegotiation (which can be
    /// initiated by either party) the new session value will not compare equal to
    /// the previous one.
    /// </summary> 
    public sealed class SessionBytes : ITlsInbound
    {
        public SessionBytes(ByteString payload)
        {
            Payload = payload;
        }

        public ByteString Payload { get; }
    }
    
    /// <summary>
    /// This is the supertype of all messages that the SslTls stage accepts on its
    /// plaintext side.
    /// </summary>
    public interface ITlsOutbound { }

    /// <summary>
    /// Send the given <see cref="ByteString"/> across the encrypted session to the peer.
    /// </summary>
    public sealed class SendBytes : ITlsOutbound
    {
        public SendBytes(ByteString payload)
        {
            Payload = payload;
        }

        public ByteString Payload { get; }
    }

    /**
     * Initiate a new session negotiation. Any [[SendBytes]] commands following
     * this one will be held back (i.e. back-pressured) until the new handshake is
     * completed, meaning that the bytes following this message will be encrypted
     * according to the requirements outlined here.
     *
     * Each of the values in this message is optional and will have the following
     * effect if provided:
     *
     * - `enabledCipherSuites` will be passed to `SSLEngine::setEnabledCipherSuites()`
     * - `enabledProtocols` will be passed to `SSLEngine::setEnabledProtocols()`
     * - `clientAuth` will be passed to `SSLEngine::setWantClientAuth()` or `SSLEngine.setNeedClientAuth()`, respectively
     * - `sslParameters` will be passed to `SSLEngine::setSSLParameters()`
     *
     * Please note that passing `clientAuth = None` means that no change is done
     * on client authentication requirements while `clientAuth = Some(ClientAuth.None)`
     * switches off client authentication.
     */
    public sealed class NegotiateNewSession : ITlsOutbound
    {
        public static readonly NegotiateNewSession Instance = new NegotiateNewSession();
        private NegotiateNewSession() { }
    }
}