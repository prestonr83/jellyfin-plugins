namespace Jellyfin.Plugin.LibraryBridge.Core;

public sealed class BridgeIndex
{
    private Snapshot _snapshot = Snapshot.Empty;
    public Snapshot Current => Volatile.Read(ref _snapshot);
    public void Publish(GroupingPlan plan) => Volatile.Write(ref _snapshot, new Snapshot(plan));
    public void Clear() => Volatile.Write(ref _snapshot, Snapshot.Empty);
}

public sealed class Snapshot
{
    public static readonly Snapshot Empty = new(GroupingPlan.Empty);
    public Snapshot(GroupingPlan plan)
    {
        Plan = plan;
        Assignments = plan.Assignments.ToDictionary(a => a.Id);
        Groups = plan.Groups.SelectMany(g => g.ItemIds.Select(id => (id, g)))
            .ToDictionary(x => x.id, x => x.g);
    }
    public GroupingPlan Plan { get; }
    public IReadOnlyDictionary<Guid, KeyAssignment> Assignments { get; }
    public IReadOnlyDictionary<Guid, BridgeGroup> Groups { get; }
}
