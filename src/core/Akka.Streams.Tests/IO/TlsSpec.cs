using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.IO;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;
using Akka.Streams.TestKit.Tests;
using Akka.TestKit;
using Xunit;
using Xunit.Abstractions;

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

        }

        [Fact]
        public void Tls_must_reliably_cancel_subscriptions_when_TransportIn_fails_early()
        {

        }

        [Fact]
        public void Tls_must_reliably_cancel_subscriptions_when_UserIn_fails_early()
        {

        }

        [Fact]
        public void Tls_must_complete_if_TLS_connection_is_truncated()
        {

        }

        [Fact]
        public void Tls_must_verify_hostname()
        {

        }

        [Fact]
        public void Tls_must_pass_data_through()
        {

        }
    }
}