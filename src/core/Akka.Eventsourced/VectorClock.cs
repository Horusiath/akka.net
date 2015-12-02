using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Akka.Eventsourced
{
    public struct VectorClock
    {
        public readonly string ProcessId;
        public readonly VectorTime CurrentTime;

        public VectorClock(string processId, VectorTime currentTime) : this()
        {
            ProcessId = processId;
            CurrentTime = currentTime;
        }

        public long CurrentLocalTime { get { return CurrentTime.LocalTime(ProcessId); } }

        [Pure]
        public VectorClock Update(VectorTime time)
        {
            return Merge(time).Tick();
        }

        [Pure]
        public VectorClock Merge(VectorTime time)
        {
            return new VectorClock(ProcessId, CurrentTime.Merge(time));
        }

        [Pure]
        public VectorClock Tick()
        {
            return new VectorClock(ProcessId, CurrentTime.Increment(ProcessId));
        }

        [Pure]
        public VectorClock Set(string processId, long tick)
        {
            return new VectorClock(ProcessId, CurrentTime.SetLocalTime(processId, tick));
        }

        public bool Covers(VectorTime emittedTimestamp, string emitterProcessId)
        {
            var self = this;
            return emittedTimestamp.Value.Any(entry => 
                entry.Key != self.ProcessId && entry.Key != emitterProcessId
                ? self.CurrentTime.LocalTime(entry.Key) < entry.Value
                : false);
        }
    }

    public struct VectorTime : IComparable<VectorTime>, IEquatable<VectorTime>, IComparable
    {
        public static readonly VectorTime Zero = new VectorTime(ImmutableDictionary<string, long>.Empty);

        public readonly IImmutableDictionary<string, long> Value;

        public VectorTime(IImmutableDictionary<string, long> value) : this()
        {
            Value = value;
        }

        [Pure]
        public VectorTime SetLocalTime(string processId, long localTime)
        {
            return new VectorTime(Value.SetItem(processId, localTime));
        }

        [Pure]
        public long LocalTime(string processId)
        {
            return Value.GetValueOrDefault(processId, 0L);
        }

        [Pure]
        public VectorTime LocalCopy(string processId)
        {
            long time;
            if (Value.TryGetValue(processId, out time))
                return new VectorTime(ImmutableDictionary.CreateRange(new[] { new KeyValuePair<string, long>(processId, time) }));
            else
                return new VectorTime(ImmutableDictionary<string, long>.Empty);
        }

        [Pure]
        public VectorTime Increment(string processId)
        {
            long time;
            if (Value.TryGetValue(processId, out time))
                return new VectorTime(Value.SetItem(processId, time + 1));
            else
                return new VectorTime(Value.SetItem(processId, 1L));
        }

        [Pure]
        public VectorTime Merge(VectorTime other)
        {
            var dict = Value.Union(other.Value)
                .Aggregate(ImmutableDictionary<string, long>.Empty, (map, pair) =>
                    map.SetItem(pair.Key, Math.Max(map.GetValueOrDefault(pair.Key, long.MinValue), pair.Value)));

            return new VectorTime(dict);
        }

        public bool Equals(VectorTime other)
        {
            return VectorTimeComparer.Instance.Equals(this, other);
        }

        public override bool Equals(object obj)
        {
            if (obj is VectorTime) return Equals((VectorTime) obj);
            return false;
        }

        public override int GetHashCode()
        {
            return VectorTimeComparer.Instance.GetHashCode();
        }

        public override string ToString()
        {
            return string.Format("VectorTime({0})",
                string.Join(" ; ", Value.Select(p => string.Format("{0}: {1}", p.Key, p.Value))));
        }

        public int CompareTo(VectorTime other)
        {
            return VectorTimeComparer.Instance.Compare(this, other);
        }

        public int CompareTo(object obj)
        {
            if (obj is VectorTime)
                return CompareTo((VectorTime) obj);
            return -1;
        }

        public static bool operator ==(VectorTime x, VectorTime y)
        {
            return x.Equals(y);
        }

        public static bool operator !=(VectorTime x, VectorTime y)
        {
            return !(x == y);
        }

        public static bool operator >(VectorTime x, VectorTime y)
        {
            return x.CompareTo(y) == 1;
        }

        public static bool operator <(VectorTime x, VectorTime y)
        {
            return x.CompareTo(y) == -1;
        }

        public static bool operator <=(VectorTime x, VectorTime y)
        {
            return x.CompareTo(y) != 1;
        }

        public static bool operator >=(VectorTime x, VectorTime y)
        {
            return x.CompareTo(y) != -1;
        }
    }

    internal class VectorTimeComparer : IComparer<VectorTime>, IEqualityComparer<VectorTime>
    {
        public static readonly VectorTimeComparer Instance = new VectorTimeComparer();

        private VectorTimeComparer() { }

        public int Compare(VectorTime x, VectorTime y)
        {
            const int none = -1;    // how to mark partial ordering
            var keys = x.Value.Keys.Union(y.Value.Keys).Distinct();
            var current = 0;
            foreach (var key in keys)
            {
                var xval = x.Value.GetValueOrDefault(key, 0L);
                var yval = y.Value.GetValueOrDefault(key, 0L);
                var s = Math.Sign(xval - yval);

                if (current == 0L) current = s;
                else if (current == -1) if(s == 1) return none;
                else if (s == -1) return none;
            }

            return current;
        }

        public bool Equals(VectorTime x, VectorTime y)
        {
            return x.Value.Keys.Union(y.Value.Keys).All(key => 
                x.Value.GetValueOrDefault(key, 0L) == y.Value.GetValueOrDefault(key, 0L));
        }

        public int GetHashCode(VectorTime obj)
        {
            throw new NotImplementedException();
        }
    }
}