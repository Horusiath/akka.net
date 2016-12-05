﻿using System;
using System.Configuration;
using System.Data.Common;
using System.Data.SQLite;
using Akka.Configuration;
using Akka.Persistence.Sql.Common.Journal;

namespace Akka.Persistence.Sqlite.Journal
{
    public sealed class BatchingSqliteJournalSetup : BatchingSqlJournalSetup
    {
        public static BatchingSqliteJournalSetup Create(Config config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config), "Sql journal settings cannot be initialized, because required HOCON section couldn't been found");

            var connectionString = config.GetString("connection-string");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = ConfigurationManager
                    .ConnectionStrings[config.GetString("connection-string-name", "DefaultConnection")]
                    .ConnectionString;
            }

            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("No connection string for Sql Event Journal was specified");

            return new BatchingSqliteJournalSetup(
                connectionString: connectionString,
                maxConcurrentOperations: config.GetInt("max-concurrent-operations", 64),
                maxBatchSize: config.GetInt("max-batch-size", 100),
                autoInitialize: config.GetBoolean("auto-initialize", false),
                connectionTimeout: config.GetTimeSpan("connection-timeout", TimeSpan.FromSeconds(30)),
                circuitBreakerSettings: CircuitBreakerSettings.Create(config.GetConfig("circuit-breaker")),
                namingConventions: new QueryConfiguration(
                    schemaName: null,
                    journalEventsTableName: config.GetString("table-name"),
                    metaTableName: config.GetString("metadata-table-name"),
                    persistenceIdColumnName: "persistence_id",
                    sequenceNrColumnName: "sequence_nr",
                    payloadColumnName: "payload",
                    manifestColumnName: "manifest",
                    timestampColumnName: "timestamp",
                    isDeletedColumnName: "is_deleted",
                    tagsColumnName: "tags",
                    orderingColumnName: "ordering",
                    timeout: config.GetTimeSpan("connection-timeout")));
        }

        public BatchingSqliteJournalSetup(string connectionString, int maxConcurrentOperations, int maxBatchSize, bool autoInitialize, 
            TimeSpan connectionTimeout, CircuitBreakerSettings circuitBreakerSettings, QueryConfiguration namingConventions) 
            : base(connectionString, maxConcurrentOperations, maxBatchSize, autoInitialize, connectionTimeout, circuitBreakerSettings, namingConventions)
        {
        }
    }

    public class BatchingSqliteJournal : BatchingSqlJournal<BatchingSqliteJournalSetup>
    {
        private DbConnection _anchor;

        public BatchingSqliteJournal(Config config) : this(BatchingSqliteJournalSetup.Create(config))
        {
        }

        public BatchingSqliteJournal(BatchingSqliteJournalSetup setup) : base(setup)
        {
        }

        protected override void PreStart()
        {
            _anchor = CreateConnection();
            _anchor.Open();
            base.PreStart();
        }

        protected override void PostStop()
        {
            base.PostStop();
            _anchor.Dispose();
        }

        protected override DbConnection CreateConnection() => new SQLiteConnection(Setup.ConnectionString);
    }
}