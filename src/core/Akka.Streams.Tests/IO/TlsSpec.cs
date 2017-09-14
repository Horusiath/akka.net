using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.IO;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;
using Akka.Streams.TestKit;
using Akka.Streams.TestKit.Tests;
using Akka.Streams.Util;
using Akka.TestKit;
using Akka.Util;
using Xunit;
using Xunit.Abstractions;
using Tcp = Akka.Streams.Dsl.Tcp;

namespace Akka.Streams.Tests.IO
{
    public class TlsSpec : TcpHelper
    {
        #region internal classes

        private class Timeout : GraphStage<FlowShape<ByteString, ByteString>>
        {
            #region logic

            private class Logic : TimerGraphStageLogic
            {
                private readonly Timeout _stage;
                private ByteString _last;

                public Logic(Timeout stage) : base(stage.Shape)
                {
                    _stage = stage;

                    SetHandler(stage.In, onPush: () =>
                    {
                        _last = Grab(stage.In);
                        Push(stage.Out, _last);
                    });

                    SetHandler(stage.Out, onPull: () => Pull(stage.In));
                }

                public override void PreStart()
                {
                    base.PreStart();
                    ScheduleOnce(new object(), _stage.Duration);
                }

                protected internal override void OnTimer(object timerKey)
                {
                    FailStage(new TimeoutException($"Timeout expired, last element was {_last}"));
                }
            }

            #endregion

            public Inlet<ByteString> In { get; } = new Inlet<ByteString>("in");
            public Outlet<ByteString> Out { get; } = new Outlet<ByteString>("out");
            public override FlowShape<ByteString, ByteString> Shape { get; }
            public TimeSpan Duration { get; }
            public ActorSystem System { get; }

            public Timeout(TimeSpan duration, ActorSystem system)
            {
                Duration = duration;
                System = system;
                Shape = new FlowShape<ByteString, ByteString>(In, Out);
            }

            protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);
        }

        public abstract class Named
        {
            public virtual string Name { get; }

            protected Named()
            {
                Name = new string(GetType().Name
                    .Reverse()
                    .SkipWhile(c => "$0123456789".IndexOf(c) != -1)
                    .TakeWhile(c => c != '+')
                    .Reverse()
                    .ToArray());
            }
        }

        public abstract class CommunicationSetup : Named
        {
            public abstract Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(
                TlsSpec spec,
                TlsClosing leftClosing,
                TlsClosing rightClosing,
                Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs);
            public virtual void Cleanup() { }
        }

        public sealed class ClientInitiates : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsSpec spec, TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs) =>
                ClientTls(leftClosing).Atop(ServerTls(rightClosing).Reversed()).Join(rhs);
        }

        public sealed class ServerInitiates : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsSpec spec, TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs) =>
                ServerTls(leftClosing).Atop(ClientTls(rightClosing).Reversed()).Join(rhs);
        }

        public sealed class ClientInitiatesViaTcp : CommunicationSetup
        {
            private Tcp.ServerBinding _binding;

            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsSpec spec, TlsClosing leftClosing,
                TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                _binding = spec.Server(ServerTls(rightClosing).Reversed().Join(rhs));
                return ClientTls(leftClosing).Join(spec.Sys.TcpStream().OutgoingConnection(_binding.LocalAddress));
            }

            public override void Cleanup()
            {
                _binding.Unbind().Wait(TimeSpan.FromSeconds(2));
            }
        }

        public sealed class ServerInitiatesViaTcp : CommunicationSetup
        {
            private Tcp.ServerBinding _binding;

            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsSpec spec, TlsClosing leftClosing,
                TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                _binding = spec.Server(ClientTls(rightClosing).Reversed().Join(rhs));
                return ServerTls(leftClosing).Join(spec.Sys.TcpStream().OutgoingConnection(_binding.LocalAddress));
            }

            public override void Cleanup()
            {
                _binding.Unbind().Wait(TimeSpan.FromSeconds(2));
            }
        }

        public abstract class PayloadScenario : Named
        {
            protected PayloadScenario()
            {
                Flow = Akka.Streams.Dsl.Flow.Create<ITlsInbound>()
                    .Select(x =>
                    {
                        switch (x)
                        {
                            case SessionTruncated _: return (ITlsOutbound)new SendBytes(ByteString.FromString("TRUNCATED"));
                            case SessionBytes sb: return new SendBytes(sb.Payload);
                            default: throw new NotSupportedException("Should never happen");
                        }
                    });
            }

            public virtual Flow<ITlsInbound, ITlsOutbound, NotUsed> Flow { get; }
            public virtual TlsClosing LeftClosing => TlsClosing.IgnoreComplete;
            public virtual TlsClosing RightClosing => TlsClosing.IgnoreComplete;

            public abstract IEnumerable<ITlsOutbound> Inputs { get; }
            public abstract ByteString Output { get; }

            protected SendBytes Send(string str) => new SendBytes(ByteString.FromString(str));
            protected SendBytes Send(char ch) => new SendBytes(ByteString.FromBytes(BitConverter.GetBytes(ch)));
        }

        public sealed class SingleBytes : PayloadScenario
        {
            public const string Str = "0123456789";
            public override IEnumerable<ITlsOutbound> Inputs { get; } = Str.Select(ch => new SendBytes(ByteString.FromBytes(BitConverter.GetBytes(ch))));
            public override ByteString Output { get; } = ByteString.FromString(Str);
        }

        public sealed class MediumMessages : PayloadScenario
        {
            public static readonly string[] Strs = "0123456789".Select(d => new string(Enumerable.Repeat(d, ThreadLocalRandom.Current.Next(9000) + 1000).ToArray())).ToArray();
            public override IEnumerable<ITlsOutbound> Inputs { get; } = Strs.Select(s => new SendBytes(ByteString.FromString(s)));
            public override ByteString Output { get; } = ByteString.FromString(string.Join("", Strs));
        }

        public sealed class LargeMessages : PayloadScenario
        {
            // TLS max packet size is 16384 bytes
            public static readonly string[] Strs = "0123456789".Select(d => new string(Enumerable.Repeat(d, ThreadLocalRandom.Current.Next(9000) + 17000).ToArray())).ToArray();
            public override IEnumerable<ITlsOutbound> Inputs { get; } = Strs.Select(s => new SendBytes(ByteString.FromString(s)));
            public override ByteString Output { get; } = ByteString.FromString(string.Join("", Strs));
        }

        public sealed class EmptyBytesFirst : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; } = new[] { ByteString.Empty, ByteString.FromString("hello") }.Select(x => new SendBytes(x));
            public override ByteString Output { get; } = ByteString.FromString("hello");
        }

        public sealed class EmptyBytesInTheMiddle : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; } = new[] { ByteString.FromString("hello"), ByteString.Empty, ByteString.FromString(" world") }.Select(x => new SendBytes(x));
            public override ByteString Output { get; } = ByteString.FromString("hello world");
        }

        public sealed class EmptyBytesLast : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; } = new[] { ByteString.FromString("hello"), ByteString.Empty }.Select(x => new SendBytes(x));
            public override ByteString Output { get; } = ByteString.FromString("hello");
        }

        public sealed class CancellingRHS : PayloadScenario
        {
            private static readonly string Str = string.Join("", Enumerable.Repeat("abcdef", 100));
            public CancellingRHS()
            {
                Inputs = Str.Select(this.Send);
                var superFlow = base.Flow;
                Flow = Akka.Streams.Dsl.Flow.Create<ITlsInbound>()
                    .SelectMany(x =>
                    {
                        switch (x)
                        {
                            case SessionTruncated _: return new[] { x } as IEnumerable<ITlsInbound>;
                            case SessionBytes sb:
                                return sb.Payload.Select(b => new SessionBytes(ByteString.FromBytes(new[] { b })));
                            default: throw new NotSupportedException("Should never happen");
                        }
                    })
                    .Take(5)
                    .SelectAsync(5, async x =>
                    {
                        await Task.Delay(500);
                        return x;
                    })
                    .Via(superFlow);
            }

            public override Flow<ITlsInbound, ITlsOutbound, NotUsed> Flow { get; }
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; } = ByteString.FromString(new string(Str.Take(5).ToArray()));
            public override TlsClosing RightClosing => TlsClosing.IgnoreCancel;
        }

        public sealed class CancellingRHSIgnoresBoth : PayloadScenario
        {
            private static readonly string Str = string.Join("", Enumerable.Repeat("abcdef", 100));
            public CancellingRHSIgnoresBoth()
            {
                Inputs = Str.Select(this.Send);
                var superFlow = base.Flow;
                Flow = Akka.Streams.Dsl.Flow.Create<ITlsInbound>()
                    .SelectMany(x =>
                    {
                        switch (x)
                        {
                            case SessionTruncated _: return new[] { x } as IEnumerable<ITlsInbound>;
                            case SessionBytes sb:
                                return sb.Payload.Select(b => new SessionBytes(ByteString.FromBytes(new[] { b })));
                            default: throw new NotSupportedException("Should never happen");
                        }
                    })
                    .Take(5)
                    .SelectAsync(5, async x =>
                    {
                        await Task.Delay(500);
                        return x;
                    })
                    .Via(superFlow);
            }

            public override Flow<ITlsInbound, ITlsOutbound, NotUsed> Flow { get; }
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; } = ByteString.FromString(new string(Str.Take(5).ToArray()));
            public override TlsClosing RightClosing => TlsClosing.IgnoreBoth;
        }

        public sealed class LHSIgnoresBoth : PayloadScenario
        {
            private const string Str = "0123456789";
            public override IEnumerable<ITlsOutbound> Inputs { get; } = Str.Select(x => new SendBytes(ByteString.FromBytes(BitConverter.GetBytes(x))));
            public override ByteString Output { get; } = ByteString.FromString(Str);
            public override TlsClosing LeftClosing => TlsClosing.IgnoreBoth;
        }

        public sealed class BothSidesIgnoreBoth : PayloadScenario
        {
            private const string Str = "0123456789";
            public override IEnumerable<ITlsOutbound> Inputs { get; } = Str.Select(x => new SendBytes(ByteString.FromBytes(BitConverter.GetBytes(x))));
            public override ByteString Output { get; } = ByteString.FromString(Str);
            public override TlsClosing LeftClosing => TlsClosing.IgnoreBoth;
            public override TlsClosing RightClosing => TlsClosing.IgnoreBoth;
        }

        public sealed class SessionRenegotiationBySender : PayloadScenario
        {
            public SessionRenegotiationBySender()
            {
                Inputs = new ITlsOutbound[] { Send("hello"), NegotiateNewSession.Instance, Send("world") };
                Output = ByteString.FromString("helloNEWSESSIONworld");
            }

            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        // difference is that the RHS engine will now receive the handshake while trying to send
        public sealed class SessionRenegotiationByReceiver : PayloadScenario
        {
            private static readonly string Str = string.Join("", Enumerable.Repeat("0123456789", 100));

            public SessionRenegotiationByReceiver()
            {
                Inputs = Str.Select(Send)
                    .Union(new ITlsOutbound[]{NegotiateNewSession.Instance})
                    .Union("hello world".Select(Send));
                Output = ByteString.FromString(Str + "NEWSESSIONhello world");
            }

            public override IEnumerable<ITlsOutbound> Inputs { get; } 
            public override ByteString Output { get; }
        }

        private struct TestCaseGenerator : IEnumerable<object[]>
        {
            private static readonly CommunicationSetup[] CommunicationPatterns =
            {
                new ClientInitiates(),
                new ServerInitiates(),
                new ClientInitiatesViaTcp(),
                new ServerInitiatesViaTcp(),
            };

            private static readonly PayloadScenario[] Scenarios =
            {
                new SingleBytes(),
                new MediumMessages(),
                new LargeMessages(),
                new EmptyBytesFirst(),
                new EmptyBytesInTheMiddle(),
                new EmptyBytesLast(),
                new CancellingRHS(),
                new SessionRenegotiationBySender(),
                new SessionRenegotiationByReceiver(),
            };

            public IEnumerator<object[]> GetEnumerator()
            {
                foreach (var setup in CommunicationPatterns)
                {
                    foreach (var scenario in Scenarios)
                    {
                        yield return new object[] { setup, scenario };
                    }
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        #endregion

        private readonly ActorMaterializer _materializer;

        public TlsSpec(ITestOutputHelper helper) : base(@"", helper)
        {
            _materializer = Sys.Materializer();
        }

        private Tcp.ServerBinding Server(Flow<ByteString, ByteString, NotUsed> flow)
        {
            var serverTask = Sys.TcpStream().Bind("localhost", 0)
                .To(Sink.ForEach<Tcp.IncomingConnection>(c => c.Flow.Join(flow).Run(_materializer)))
                .Run(_materializer);

            serverTask.Wait(TimeSpan.FromSeconds(2));
            return serverTask.Result;
        }

        private static readonly X509Certificate2 Certificate = new X509Certificate2();
        private static BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> ClientTls(TlsClosing closing) =>
            Tls.Client(TlsSettings.Client("localhost", closing));

        private static BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> BadClientTls(TlsClosing closing) =>
            Tls.Client(TlsSettings.Client("localhost", closing, certificates: new X509Certificate2("/badpath")));

        private static BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> ServerTls(TlsClosing closing) =>
            Tls.Server(TlsSettings.Server(Certificate, closing));

        [Theory]
        [ClassData(typeof(TestCaseGenerator))]
        public void Tls_must_work_in_different_modes_while_sending_different_payload_scenarios(CommunicationSetup setup, PayloadScenario scenario)
        {
            this.AssertAllStagesStopped(() =>
            {
                var f = Source.From(scenario.Inputs)
                    .Via(setup.DecorateFlow(this, scenario.LeftClosing, scenario.RightClosing, scenario.Flow))
                    .Collect(x => x as SessionBytes)
                    .Scan(ByteString.Empty, (acc, bytes) => acc + bytes.Payload)
                    .Via(new Timeout(TimeSpan.FromSeconds(6), Sys))
                    .SkipWhile(x => x.Count < scenario.Output.Count)
                    .RunWith(Sink.First<ByteString>(), _materializer);

                f.Wait(TimeSpan.FromSeconds(8));
                var actual = f.Result;
                actual.ToString().ShouldBe(scenario.Output.ToString());

                setup.Cleanup();
            }, _materializer);
        }

        [Fact]
        public void Tls_must_emit_an_error_if_the_TLS_handshake_fails_certificate_check()
        {
            this.AssertAllStagesStopped(() =>
            {
                var getError = Flow.Create<ITlsInbound>()
                    .Select(i => (Either<ITlsInbound, Exception>)Either.Left(i))
                    .Recover(e => new Option<Either<ITlsInbound, Exception>>(Either.Right(e)))
                    .Collect(e => e.IsRight ? (Exception)e.Value : null)
                    .ToMaterialized(Sink.First<Exception>(), Keep.Right);

                var simple = Flow.FromSinkAndSource(getError, Source.Maybe<ITlsOutbound>(), Keep.Left);


                // The creation of actual TCP connections is necessary. It is the easiest way to decouple the client and server
                // under error conditions, and has the bonus of matching most actual SSL deployments.
                var (server, serverErr) = Sys.TcpStream()
                    .Bind("localhost", 0)
                    .SelectAsync(1, conn => conn.Flow
                        .JoinMaterialized(ServerTls(TlsClosing.IgnoreBoth).Reversed().JoinMat(simple, Keep.Right),
                            Keep.Right).Run(_materializer))
                    .ToMaterialized(Sink.First<Exception>(), Keep.Both)
                    .Run(_materializer);

                var second = TimeSpan.FromSeconds(1);

                server.Wait(second).ShouldBe(true);

                var clientErr = simple.Join(BadClientTls(TlsClosing.IgnoreBoth))
                    .Join(Sys.TcpStream().OutgoingConnection(server.Result.LocalAddress))
                    .Run(_materializer);

                serverErr.Wait(second).ShouldBe(true);
                clientErr.Wait(second).ShouldBe(true);

                // TODO: assert exception is SSL exception type
                //Await.result(serverErr, 1.second).getMessage should include("certificate_unknown")
                //Await.result(clientErr, 1.second).getMessage should equal("General SSLEngine problem")
            }, _materializer);
        }

        [Fact]
        public void Tls_must_reliably_cancel_subscriptions_when_TransportIn_fails_early()
        {
            this.AssertAllStagesStopped(() =>
            {
                var exn = new Exception("hello");
                var (sub, out1, out2) = RunnableGraph.FromGraph(GraphDsl.Create(Source.AsSubscriber<ITlsOutbound>(),
                    Sink.First<ByteString>(), Sink.First<ITlsInbound>(),
                    combineMaterializers: Tuple.Create, buildBlock: (b, s, o1, o2) =>
                       {
                           var source = Source.Failed<ByteString>(exn);
                           var tls = b.Add(ClientTls(TlsClosing.EagerClose));
                           b.From(s).To(tls.Inlet1);
                           b.From(tls.Outlet1).To(o1);
                           b.To(o2).From(tls.Outlet2);
                           b.To(tls.Inlet2).From(source.Shape);

                           return ClosedShape.Instance;
                       }))
                    .Run(_materializer);

                var second = TimeSpan.FromSeconds(1);
                out1.Wait(second);
                out1.Exception.ShouldBe(exn);

                out2.Wait(second);
                out2.Exception.ShouldBe(exn);

                Thread.Sleep(500);
                var pub = this.CreatePublisherProbe<ITlsOutbound>();
                pub.Subscribe(sub);
                pub.ExpectSubscription().ExpectCancellation();

            }, _materializer);
        }

        [Fact]
        public void Tls_must_reliably_cancel_subscriptions_when_UserIn_fails_early()
        {
            this.AssertAllStagesStopped(() =>
            {
                var exn = new Exception("hello");
                var (sub, out1, out2) = RunnableGraph.FromGraph(GraphDsl.Create(Source.AsSubscriber<ByteString>(),
                        Sink.First<ByteString>(), Sink.First<ITlsInbound>(),
                        combineMaterializers: Tuple.Create, buildBlock: (b, s, o1, o2) =>
                        {
                            var failed = Source.Failed<ITlsOutbound>(exn);
                            var tls = b.Add(ClientTls(TlsClosing.EagerClose));
                            b.From(failed.Shape).To(tls.Inlet1);
                            b.From(tls.Outlet1).To(o1);
                            b.To(o2).From(tls.Outlet2);
                            b.To(tls.Inlet2).From(s);

                            return ClosedShape.Instance;
                        }))
                    .Run(_materializer);

                var second = TimeSpan.FromSeconds(1);
                out1.Wait(second);
                out1.Exception.ShouldBe(exn);

                out2.Wait(second);
                out2.Exception.ShouldBe(exn);

                Thread.Sleep(500);
                var pub = this.CreatePublisherProbe<ByteString>();
                pub.Subscribe(sub);
                pub.ExpectSubscription().ExpectCancellation();

            }, _materializer);
        }

        [Fact]
        public void Tls_must_complete_if_TLS_connection_is_truncated()
        {
            this.AssertAllStagesStopped(() =>
            {
                var ks = KillSwitches.Shared("ks");
                var scenario = new SingleBytes();

                var terminator = BidiFlow.FromFlows(Flow.Create<ByteString>(), ks.Flow<ByteString>());
                var outFlow = ClientTls(scenario.LeftClosing)
                    .Atop(terminator)
                    .Atop(ServerTls(scenario.RightClosing).Reversed())
                    .Join(scenario.Flow);

                var inFlow = Flow.Create<ITlsInbound>()
                    .Collect(x => x is SessionBytes s ? s.Payload : null)
                    .Scan(ByteString.Empty, (x, y) => x + y)
                    .Via(new Timeout(TimeSpan.FromSeconds(6), Sys))
                    .SkipWhile(x => x.Count < scenario.Output.Count);

                var f = Source.From(scenario.Inputs)
                    .Via(outFlow)
                    .Via(inFlow)
                    .Select(result =>
                    {
                        ks.Shutdown();
                        return result;
                    })
                    .RunWith(Sink.Last<ByteString>(), _materializer);

                f.Wait(TimeSpan.FromSeconds(8)).ShouldBe(true);
                f.Result.ToString().ShouldBe(scenario.Output.ToString());

            }, _materializer);
        }

        [Fact]
        public void Tls_must_verify_hostname()
        {
            Task Run(string hostname)
            {
                var rhs = Flow.Create<ITlsInbound>()
                    .Select(x =>
                    {
                        switch (x)
                        {
                            case SessionTruncated _: return (ITlsOutbound)new SendBytes(ByteString.Empty);
                            case SessionBytes sb: return new SendBytes(sb.Payload);
                            default: throw new NotSupportedException("should not happen");
                        }
                    });

                var clientTls = Tls.Client(TlsSettings.Client(hostname + ":80"));
                var flow = clientTls.Atop(ServerTls(TlsClosing.EagerClose).Reversed()).Join(rhs);

                return Source.Single<ITlsOutbound>(new SendBytes(ByteString.Empty)).Via(flow).RunWith(Sink.Ignore<ITlsInbound>(), _materializer);
            }

            this.AssertAllStagesStopped(() =>
            {
                var t1 = Run("akka-remote");
                t1.Wait(TimeSpan.FromSeconds(3)).ShouldBe(true);
                Intercept<Exception>(() =>
                {
                    var t2 = Run("unknown.example.org");
                    t2.Wait(TimeSpan.FromSeconds(3)).ShouldBe(true);
                });

            }, _materializer);
        }

        [Fact]
        public void Tls_must_pass_data_through()
        {
            var f = Source.From(Enumerable.Range(1, 3))
                .Select(b => (ITlsOutbound)new SendBytes(ByteString.FromBytes(BitConverter.GetBytes(b))))
                .Via(Tls.Server(TlsSettings.Server(Certificate)).Join(Flow.Create<ByteString>()))
                .Grouped(10)
                .RunWith(Sink.First<IEnumerable<ITlsInbound>>(), _materializer);
            f.Wait(TimeSpan.FromSeconds(3)).ShouldBe(true);
            var result = f.Result;

            result.Select(x => ((SessionBytes)x).Payload).ShouldBe(Enumerable.Range(1, 3).Select(x => ByteString.FromBytes(BitConverter.GetBytes(x))));
        }
    }
}