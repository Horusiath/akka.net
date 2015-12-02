using System.Threading.Tasks;

namespace Akka.Eventsourced.Snapshotting
{
    public interface ISnapshotStore
    {
        Task DeleteAsync(long lowerSequenceNr);
        Task SaveAsync(Snapshot snapshot);
        Task<Snapshot> LoadAsync(string emitterId);
    }
}