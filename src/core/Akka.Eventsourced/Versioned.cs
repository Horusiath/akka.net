using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Akka.Eventsourced
{
    /// <summary>
    /// A versioned value of type <typeparamref name="TValue"/>.
    /// </summary>
    public struct Versioned<TValue> : IEquatable<Versioned<TValue>>
    {
        public readonly TValue Value;
        public readonly VectorTime UpdateTimestamp;
        public readonly string Creator;

        /// <summary>
        /// Creates new instance of the <see cref="Versioned{TValue}"/> value.
        /// </summary>
        /// <param name="value">The value</param>
        /// <param name="updateTimestamp">Timestamp of the event that caused this version.</param>
        /// <param name="creator">Creator, that caused this version event.</param>
        public Versioned(TValue value, VectorTime updateTimestamp, string creator = null)
        {
            Value = value;
            UpdateTimestamp = updateTimestamp;
            Creator = creator ?? string.Empty;
        }

        public bool Equals(Versioned<TValue> other)
        {
            return Equals(Creator, other.Creator)
                   && Equals(Value, other.Value)
                   && Equals(UpdateTimestamp, other.UpdateTimestamp);
        }

        public override bool Equals(object obj)
        {
            if (obj is Versioned<TValue>) return Equals((Versioned<TValue>) obj);
            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = EqualityComparer<TValue>.Default.GetHashCode(Value);
                hashCode = (hashCode * 397) ^ UpdateTimestamp.GetHashCode();
                hashCode = (hashCode * 397) ^ Creator.GetHashCode();
                return hashCode;
            }
        }
    }

    public interface IConcurrentVersions<TVersioned, in TUpdated>
    {
        IEnumerable<Versioned<TVersioned>> All { get; }
        string Owner { get; }

        [Pure]
        IConcurrentVersions<TVersioned, TUpdated> Update(TUpdated updated, VectorTime updatedTimestamp, string creator);

        [Pure]
        IConcurrentVersions<TVersioned, TUpdated> Resolve(VectorTime selectedTimestamp, VectorTime updatedTimestamp);

        [Pure]
        IConcurrentVersions<TVersioned, TUpdated> WithOwner(string owner);
    }

    public static class VersionedExtensions
    {
        public static IConcurrentVersions<TVersioned, TUpdated> Resolve<TVersioned, TUpdated>(
            this IConcurrentVersions<TVersioned, TUpdated> versions, VectorTime selectedTimestamp)
        {
            var vt = versions.All
                .Select(x => x.UpdateTimestamp)
                .Aggregate(VectorTime.Zero, (time, vectorTime) => time.Merge(vectorTime));

            return versions.Resolve(selectedTimestamp, vt);
        }

        public static bool IsConflicting<TVersioned, TUpdated>(this IConcurrentVersions<TVersioned, TUpdated> versions)
        {
            return versions.All.Take(2).Count() > 1;
        }
    }

    public class ConcurrentVersionsList<TVal> : IConcurrentVersions<TVal, TVal>
    {
        private readonly IImmutableList<Versioned<TVal>> _list;

        public ConcurrentVersionsList(IImmutableList<Versioned<TVal>> list, string owner = null)
        {
            _list = list;
            Owner = owner ?? string.Empty;
        }

        public IEnumerable<Versioned<TVal>> All { get { return _list; } }
        public string Owner { get; private set; }

        [Pure]
        public IConcurrentVersions<TVal, TVal> Update(TVal updated, VectorTime updatedTimestamp, string creator)
        {
            var r = _list.Aggregate(Tuple.Create(ImmutableList<Versioned<TVal>>.Empty as IImmutableList<Versioned<TVal>>, false), (t, versioned) =>
            {
                if (t.Item2) return Tuple.Create(t.Item1.Add(versioned), true);
                else
                {
                    if (updatedTimestamp > versioned.UpdateTimestamp)
                        return Tuple.Create(t.Item1.Add(new Versioned<TVal>(updated, updatedTimestamp, creator)), true);
                    else if (updatedTimestamp < versioned.UpdateTimestamp)
                        return Tuple.Create(t.Item1.Add(versioned), true);
                    else
                        return Tuple.Create(t.Item1.Add(versioned), false);
                }
            });

            if(r.Item2) return new ConcurrentVersionsList<TVal>(r.Item1, Owner);
            else return new ConcurrentVersionsList<TVal>(r.Item1.Add(new Versioned<TVal>(updated, updatedTimestamp, creator)), Owner);
        }

        [Pure]
        public IConcurrentVersions<TVal, TVal> Resolve(VectorTime selectedTimestamp, VectorTime updatedTimestamp)
        {
            var r = _list.Aggregate(ImmutableList<Versioned<TVal>>.Empty as IImmutableList<Versioned<TVal>>,
                (acc, versioned) =>
                {
                    if (versioned.UpdateTimestamp == selectedTimestamp)
                        return acc.Add(new Versioned<TVal>(versioned.Value, updatedTimestamp, versioned.Creator));
                    else if (versioned.UpdateTimestamp.CompareTo(updatedTimestamp) == -1)
                        return acc.Add(versioned);
                    else
                        return acc;
                });

            return new ConcurrentVersionsList<TVal>(r);
        }

        [Pure]
        public IConcurrentVersions<TVal, TVal> WithOwner(string owner)
        {
            return new ConcurrentVersionsList<TVal>(_list, owner);
        }
    }

    public class ConcurrentVersionsTree<TKey, TVal> : IConcurrentVersions<TKey, TVal>
    {
        #region tree node definition
        [Serializable]
        public sealed class Node
        {
             
        }
        #endregion

        public IEnumerable<Versioned<TKey>> All { get; }
        public string Owner { get; }

        [Pure]
        public IConcurrentVersions<TKey, TVal> Update(TVal updated, VectorTime updatedTimestamp, string creator)
        {
            throw new NotImplementedException();
        }

        [Pure]
        public IConcurrentVersions<TKey, TVal> Resolve(VectorTime selectedTimestamp, VectorTime updatedTimestamp)
        {
            throw new NotImplementedException();
        }

        [Pure]
        public IConcurrentVersions<TKey, TVal> WithOwner(string owner)
        {
            throw new NotImplementedException();
        }
    }
}