//-----------------------------------------------------------------------
// <copyright file="JournalDbEngine.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Typesafe Inc. <http://www.typesafe.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Akka.Actor;

namespace Akka.Persistence.Sql.Common.Journal
{
    /// <summary>
    /// Class used for storing intermediate result of the <see cref="IPersistentRepresentation"/>
    /// in form which is ready to be stored directly in the SQL table.
    /// </summary>
    public sealed class JournalEntry
    {
        internal static readonly string[] EmptyTags = new string[0];

        public readonly string PersistenceId;
        public readonly long SequenceNr;
        public readonly string Manifest;
        public readonly object Payload;
        public readonly IActorRef Sender;
        public readonly string[] Tags;

        public JournalEntry(string persistenceId, long sequenceNr, string manifest, object payload, IActorRef sender, string[] tags = null)
        {
            PersistenceId = persistenceId;
            SequenceNr = sequenceNr;
            Manifest = manifest;
            Payload = payload;
            Sender = sender;
            Tags = tags ?? EmptyTags;
        }
    }
    
    /// <summary>
    /// Class used for storing whole intermediate set of write changes to be applied within single SQL transaction.
    /// </summary>
    public sealed class WriteBatch
    {
        public readonly JournalEntry[] Entries;

        public WriteBatch(JournalEntry[] entries)
        {
            Entries = entries;
        }
    }

    public sealed class WriteBatchSuccess
    {
        public readonly ImmutableHashSet<string> PersistenceIds;
        public readonly ImmutableHashSet<string> Tags;

        public WriteBatchSuccess(ImmutableHashSet<string> persistenceIds, ImmutableHashSet<string> tags)
        {
            PersistenceIds = persistenceIds;
            Tags = tags;
        }
    }

    public sealed class WriteBatchFailure
    {
        public readonly Exception Cause;

        public WriteBatchFailure(Exception cause)
        {
            Cause = cause;
        }
    }

    /// <summary>
    /// Message type containing set of all <see cref="Eventsourced.PersistenceId"/> received from the database.
    /// </summary>
    public sealed class AllPersistenceIds
    {
        public readonly ImmutableArray<string> Ids;

        public AllPersistenceIds(ImmutableArray<string> ids)
        {
            Ids = ids;
        }
    }
}