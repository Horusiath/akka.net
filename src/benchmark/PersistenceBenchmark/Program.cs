//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Diagnostics;

namespace PersistenceBenchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            var bench = new EventsourcedSqliteBenchmark();
            bench.Setup();
            var stopwatch = new Stopwatch();
            GC.Collect(2, GCCollectionMode.Forced);
            stopwatch.Start();
            bench.Sqlite_10_eventsourced_actors_write_1000_events_per_actor();
            var elapsed = stopwatch.Elapsed;
            bench.Cleanup();

            Console.WriteLine(
                $"{nameof(bench.Sqlite_10_eventsourced_actors_write_1000_events_per_actor)}\n\tTotal time: {elapsed}\n\tAvg. time: {new TimeSpan(elapsed.Ticks / 10000)}\n\tOps. per second: {10000 / elapsed.TotalSeconds}");
            Console.ReadLine();
        }
    }
}
