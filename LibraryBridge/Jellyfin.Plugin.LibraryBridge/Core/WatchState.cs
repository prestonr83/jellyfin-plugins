namespace Jellyfin.Plugin.LibraryBridge.Core;

public sealed record WatchState(bool Played, long PositionTicks, int PlayCount, DateTime? LastPlayedDate)
{
    public static WatchState Reconcile(IEnumerable<WatchState> states)
    {
        var values = states.ToArray();
        var latest = values.OrderByDescending(s => s.LastPlayedDate).ThenByDescending(s => s.PositionTicks).First();
        // Existing history is reconciled once per refresh conservatively: never discard a completed watch.
        var played = values.Any(s => s.Played);
        return new(played, played ? 0 : latest.PositionTicks, values.Max(s => s.PlayCount), values.Max(s => s.LastPlayedDate));
    }
    public long PositionFor(long? sourceDuration, long? targetDuration)
    {
        if (Played || PositionTicks <= 0) return 0;
        var position = PositionTicks;
        if (sourceDuration > 0 && targetDuration > 0)
            position = (long)Math.Min(long.MaxValue, (decimal)PositionTicks * targetDuration.Value / sourceDuration.Value);
        return targetDuration > 0 ? Math.Min(position, Math.Max(0, targetDuration.Value - 1)) : position;
    }
}
