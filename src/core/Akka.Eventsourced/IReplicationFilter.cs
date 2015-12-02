using System.Collections.Generic;
using System.Linq;

namespace Akka.Eventsourced
{
    public interface IReplicationFilterSerializable { }

    public interface IReplicationFilter
    {
        bool Apply(DurableEvent e);
    }

    public static class ReplicationFilterExtensions
    {
        public static IReplicationFilter And(this IReplicationFilter self, IReplicationFilter filter)
        {
            if (self is AndFilter)
            {
                var and = (AndFilter)self;
                return new AndFilter(and.Filters.Union(new[] { filter }));
            }

            return new AndFilter(new[] { self, filter });
        }
        public static IReplicationFilter Or(this IReplicationFilter self, IReplicationFilter filter)
        {
            if (self is OrFilter)
            {
                var and = (OrFilter)self;
                return new OrFilter(and.Filters.Union(new[] { filter }));
            }

            return new OrFilter(new[] { self, filter });
        }
    }

    public struct AndFilter : IReplicationFilter, IReplicationFilterSerializable
    {
        public readonly IEnumerable<IReplicationFilter> Filters;

        public AndFilter(IEnumerable<IReplicationFilter> filters)
        {
            Filters = filters;
        }

        public bool Apply(DurableEvent e)
        {
            foreach (var filter in Filters)
                if (!filter.Apply(e)) return false;

            return true;
        }
    }

    public struct OrFilter : IReplicationFilter, IReplicationFilterSerializable
    {
        public readonly IEnumerable<IReplicationFilter> Filters;

        public OrFilter(IEnumerable<IReplicationFilter> filters)
        {
            Filters = filters;
        }

        public bool Apply(DurableEvent e)
        {
            foreach (var filter in Filters)
                if (filter.Apply(e)) return true;

            return false;
        }
    }

    /// <summary>
    /// Filter representing lack on any filters. Always evaluates to true.
    /// </summary>
    public sealed class NoFilter : IReplicationFilter, IReplicationFilterSerializable
    {
        public static readonly NoFilter Instance = new NoFilter();

        private NoFilter()
        {
        }

        public bool Apply(DurableEvent e)
        {
            return true;
        }
    }
}