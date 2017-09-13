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
                TlsClosing leftClosing,
                TlsClosing rightClosing,
                Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs);
            public virtual void Cleanup() { }
        }

        public sealed class ClientInitiates : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                throw new NotImplementedException();
            }
        }

        public sealed class ServerInitiates : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                throw new NotImplementedException();
            }
        }

        public sealed class ClientInitiatesViaTcp : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                throw new NotImplementedException();
            }
        }

        public sealed class ServerInitiatesViaTcp : CommunicationSetup
        {
            public override Flow<ITlsOutbound, ITlsInbound, NotUsed> DecorateFlow(TlsClosing leftClosing, TlsClosing rightClosing, Flow<ITlsInbound, ITlsOutbound, NotUsed> rhs)
            {
                throw new NotImplementedException();
            }
        }

        public abstract class PayloadScenario : Named
        {
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
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class MediumMessages : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class LargeMessages : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class EmptyBytesFirst : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class EmptyBytesInTheMiddle : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class EmptyBytesLast : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class CancellingRHS : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class CancellingRHSIgnoresBoth : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class LHSIgnoresBoth : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class BothSidesIgnoreBoth : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class SessionRenegotiationBySender : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class SessionRenegotiationByReceiver : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class SessionRenegotiationFirstOne : PayloadScenario
        {
            public override IEnumerable<ITlsOutbound> Inputs { get; }
            public override ByteString Output { get; }
        }

        public sealed class SessionRenegotiationFirstTwo : PayloadScenario
        {
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
                new SessionRenegotiationFirstOne(),
                new SessionRenegotiationFirstTwo(),
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

        private static readonly X509Certificate2 Certificate = new X509Certificate2();
        private BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> ClientTls(TlsClosing closing) =>
            Tls.Client(TlsSettings.Client("localhost"));

        private BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> BadClientTls(TlsClosing closing) =>
            Tls.Client(TlsSettings.Client("localhost", certificates: new X509Certificate2("/badpath")));

        private BidiFlow<ITlsOutbound, ByteString, ByteString, ITlsInbound, NotUsed> ServerTls(TlsClosing closing) =>
            Tls.Server(TlsSettings.Server(Certificate));

        [Theory]
        [ClassData(typeof(TestCaseGenerator))]
        public void Tls_must_work_in_different_modes_while_sending_different_payload_scenarios(CommunicationSetup setup, PayloadScenario scenario)
        {
            this.AssertAllStagesStopped(() =>
            {
                var f = Source.From(scenario.Inputs)
                    .Via(setup.DecorateFlow(scenario.LeftClosing, scenario.RightClosing, scenario.Flow))
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