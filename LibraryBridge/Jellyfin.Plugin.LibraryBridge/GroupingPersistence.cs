using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.LibraryBridge;

/// <summary>Maintains keys at the normal persistence boundary, including during metadata refreshes.</summary>
public sealed class GroupingPersistence(IItemPersistenceService inner, BridgeIndex index, RecoveryJournal journal, Func<bool>? enabled = null)
    : IItemPersistenceService
{
    private static readonly AsyncLocal<int> Suppression = new();
    public static IDisposable Suppress()
    {
        Suppression.Value++;
        return new Suspension();
    }
    private sealed class Suspension : IDisposable { public void Dispose() => Suppression.Value--; }

    /// <summary>Persist only grouping keys that differ; reapplying an existing plan is a read-only operation.</summary>
    public static void SavePlan(IItemPersistenceService persistence, ILibraryManager library, GroupingPlan plan, CancellationToken cancellationToken)
    {
        var pending = new List<BaseItem>();
        foreach (var assignment in plan.Assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = library.GetItemById(assignment.Id);
            if (item is null || !Matches(item, assignment)) continue;
            if ((assignment.PresentationKey is not null && item.PresentationUniqueKey != assignment.PresentationKey)
                || (assignment.SeriesKey is not null && (item as IHasSeries)?.SeriesPresentationUniqueKey != assignment.SeriesKey))
                pending.Add(item);
        }
        // Capture every original before any batch is changed, as on the initial apply.
        if (persistence is GroupingPersistence grouping) grouping.Prepare(pending, cancellationToken);
        foreach (var batch in pending.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            persistence.SaveItems(batch, cancellationToken);
        }
    }

    public void Prepare(IEnumerable<BaseItem> items, CancellationToken cancellationToken)
    {
        var snapshot = index.Current;
        journal.Capture(ReadChanges());
        IEnumerable<(KeyState Original, KeyAssignment Applied)> ReadChanges()
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!snapshot.Assignments.TryGetValue(item.Id, out var assignment) || !Matches(item, assignment)) continue;
                if ((assignment.PresentationKey is null || item.PresentationUniqueKey == assignment.PresentationKey)
                    && (assignment.SeriesKey is null || (item as IHasSeries)?.SeriesPresentationUniqueKey == assignment.SeriesKey)) continue;
                yield return (RecoveryBaseline(item, ReadKeys(item)), assignment);
            }
        }
    }

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        if (Suppression.Value > 0 || !(enabled?.Invoke() ?? Plugin.Instance?.Configuration.Enabled == true))
        {
            inner.SaveItems(items, cancellationToken);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = index.Current;
        var changes = new List<(BaseItem Item, KeyState Original, KeyAssignment Applied)>();
        foreach (var item in items)
        {
            if (!snapshot.Assignments.TryGetValue(item.Id, out var assignment)
                || !Matches(item, assignment)) continue;
            if ((assignment.PresentationKey is null || item.PresentationUniqueKey == assignment.PresentationKey)
                && (assignment.SeriesKey is null || (item as IHasSeries)?.SeriesPresentationUniqueKey == assignment.SeriesKey)) continue;
            changes.Add((item, ReadKeys(item), assignment));
        }
        if (changes.Count > 0) journal.Capture(changes.Select(c => (RecoveryBaseline(c.Item, c.Original), c.Applied)));
        foreach (var change in changes) Apply(change.Item, change.Applied);
        try { inner.SaveItems(items, cancellationToken); }
        catch
        {
            foreach (var change in changes) Restore(change.Item, change.Original);
            throw;
        }
    }
    private KeyState RecoveryBaseline(BaseItem item, KeyState state)
    {
        if (journal.Find(item.Id) is not null) return state;
        if (item is IHasSeries series && state.SeriesKey?.StartsWith("librarybridge:", StringComparison.Ordinal) == true)
        {
            var originalSeries = journal.Find(series.SeriesId)?.Original
                ?? throw new InvalidDataException($"Original series keys are missing for {series.SeriesId}.");
            var key = originalSeries.PresentationKey ?? series.SeriesId.ToString("N");
            state = state with { SeriesKey = key };
            if (item is Season && state.PresentationKey?.StartsWith("librarybridge:", StringComparison.Ordinal) == true)
                state = state with { PresentationKey = key + "-" + item.IndexNumber.GetValueOrDefault().ToString("000", System.Globalization.CultureInfo.InvariantCulture) };
        }
        return state;
    }
    public static bool Matches(BaseItem item, KeyAssignment assignment) => item.Path == assignment.Path && Kind(item) == assignment.Kind
        && (assignment.Signature is null || assignment.Signature == PlanBuilder.Signature(new CatalogItem(
            item.Id, Kind(item), item.Name, item.Path, Guid.Empty, "", 0, item.ProviderIds,
            (item as IHasSeries)?.SeriesId ?? Guid.Empty,
            item is Episode ep ? ep.ParentIndexNumber : item.IndexNumber,
            item is Episode episode ? episode.IndexNumber : null, (item as Episode)?.IndexNumberEnd,
            (item as Series)?.DisplayOrder?.Trim() ?? "")));
    public static string Kind(BaseItem item) => item switch { Movie => "Movie", Series => "Series", Season => "Season", Episode => "Episode", _ => "Other" };
    public static KeyState ReadKeys(BaseItem item) => new(item.Id, item.Path, Kind(item), item.PresentationUniqueKey, (item as IHasSeries)?.SeriesPresentationUniqueKey);
    public static void Apply(BaseItem item, KeyAssignment assignment)
    {
        if (assignment.PresentationKey is not null) item.PresentationUniqueKey = assignment.PresentationKey;
        if (item is IHasSeries series && assignment.SeriesKey is not null) series.SeriesPresentationUniqueKey = assignment.SeriesKey;
    }
    public static void Restore(BaseItem item, KeyState state)
    {
        item.PresentationUniqueKey = state.PresentationKey!;
        if (item is IHasSeries series) series.SeriesPresentationUniqueKey = state.SeriesKey!;
    }
    public void DeleteItem(params IReadOnlyList<Guid> ids) => inner.DeleteItem(ids);
    public Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken = default) => inner.SaveImagesAsync(item, cancellationToken);
    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) => inner.ReattachUserDataAsync(item, cancellationToken);
    public void UpdateInheritedValues() => inner.UpdateInheritedValues();
}
