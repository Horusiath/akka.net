using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;

namespace PersistenceBenchmark
{
    public sealed class EventsourcedSqliteBenchmark : SqliteBenchmark
    {
        public EventsourcedSqliteBenchmark()
            : base((system, x) => system.ActorOf(Props.Create(() => new EventsourcedActor(x)), x))
        {
        }

        public void Setup() => InvokeSetup();
        public void Cleanup() => InvokeCleanup();
        public void Sqlite_10_eventsourced_actors_write_1000_events_per_actor() => Test();
    }

    public sealed class CommandsourcedSqliteBenchmark : SqliteBenchmark
    {
        public CommandsourcedSqliteBenchmark()
            : base((system, x) => system.ActorOf(Props.Create(() => new CommandsourcedActor(x)), x))
        {
        }

        public void Setup() => InvokeSetup();
        public void Cleanup() => InvokeCleanup();
        public void SqlServer_10_commandsourced_actors_write_1000_events_per_actor() => Test();
    }

    public abstract class SqliteBenchmark
    {
        private readonly Func<ActorSystem, string, IActorRef> _actorFactory;
        protected ActorSystem system;
        protected IActorRef journalRef;
        protected IActorRef[] refs;
        private static readonly Config config = ConfigurationFactory.ParseString(@"
                akka {
                    serializers {
                        wire = ""Akka.Serialization.WireSerializer, Akka.Serialization.Wire""
                    }
                    serialization-bindings {
                        ""System.Object"" = wire
                    }
                    persistence {
                        publish-plugin-commands = on
                        journal {
                            plugin = ""akka.persistence.journal.sqlite""
                            sqlite {
                                class = ""Akka.Persistence.Sqlite.Journal.SqliteJournal, Akka.Persistence.Sqlite""
                                plugin-dispatcher = ""akka.actor.default-dispatcher""
                                table-name = event_journal
                                metadata-table-name = journal_metadata
                                auto-initialize = on
                                connection-string = ""FullUri=file:test-journal.db""
                            }
                        }
                    }
                }
        ");

        protected SqliteBenchmark(Func<ActorSystem, string, IActorRef> actorFactory)
        {
            _actorFactory = actorFactory;
        }

        protected void InvokeSetup()
        {
            Clean();
            system = ActorSystem.Create("SqliteBenchmark", config);
            journalRef = Persistence.Instance.Apply(system).JournalFor(null);
            refs = new IActorRef[10];
            for (int i = 0; i < refs.Length; i++)
            {
                var name = (char)(((int)'a') + i);
                refs[i] = _actorFactory(system, name.ToString());
            }

            Task.WaitAll(refs.Select(actorRef => actorRef.Ask<Initialized>(InitMsg.Instance, TimeSpan.FromSeconds(1))).Cast<Task>().ToArray());
            Console.WriteLine("Initialized actors");
        }

        private void Clean()
        {
            if (File.Exists("test-journal.db"))
                File.Delete("test-journal.db");
        }

        protected void InvokeCleanup()
        {
            system.Terminate().Wait(TimeSpan.FromSeconds(3));
            Clean();
            Console.WriteLine("Cleaned up");
        }

        protected void Test()
        {
            for (int i = 0; i < 1000; i++)
            {
                var cmd = new Cmd(i);
                foreach (var actorRef in refs)
                    actorRef.Tell(cmd);
            }

            Task.WaitAll(refs.Select(actorRef => actorRef.Ask(Complete.Instance)).Cast<Task>().ToArray());
        }
    }
}