using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed record BridgeReport(DateTime CreatedUtc, bool Applied, int GroupCount, int ItemCount,
    int PendingRecoveryItems, BridgeGroup[] Groups, string[] Warnings);

public sealed class BridgeCoordinator(Catalog catalog, BridgeIndex index, RecoveryJournal journal,
    ILibraryManager library, IItemPersistenceService persistence, WatchSynchronizer watch,
    ILogger<BridgeCoordinator> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public BridgeReport? LastReport { get; private set; }
    public string? LastError { get; private set; }

    public async Task<BridgeReport> PreviewAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = catalog.Build(Plugin.Instance!.Configuration, cancellationToken);
            return LastReport = Report(plan, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<BridgeReport> ApplyAsync(bool enable, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin has not started.");
            // Validate the journal and complete plan before enabling or changing any item.
            journal.Read();
            var plan = catalog.Build(plugin.Configuration, cancellationToken);
            if (enable)
            {
                plugin.Configuration.Enabled = true;
                plugin.SaveConfiguration();
            }
            if (!plugin.Configuration.Enabled) return LastReport = Report(plan, false);
            index.Publish(plan);
            RestoreEntries(plan.Assignments.Select(a => a.Id).ToHashSet(), cancellationToken);
            GroupingPersistence.SavePlan(persistence, library, plan, cancellationToken);
            if (plugin.Configuration.SynchronizeWatchState && plugin.Configuration.ReconcileExistingWatchState)
                watch.Reconcile(plan, cancellationToken);
            LastError = null;
            logger.LogInformation("Library Bridge applied {Groups} groups across {Items} items", plan.Groups.Length, plan.Assignments.Length);
            return LastReport = Report(plan, true);
        }
        catch (Exception error) { LastError = error.Message; throw; }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin has not started.");
            plugin.Configuration.Enabled = false;
            plugin.SaveConfiguration();
            index.Clear();
            RestoreEntries([], cancellationToken);
            LastError = null;
            LastReport = Report(GroupingPlan.Empty, false);
        }
        catch (Exception error) { LastError = error.Message; throw; }
        finally { _gate.Release(); }
    }

    private BridgeReport Report(GroupingPlan plan, bool applied) => new(DateTime.UtcNow, applied,
        plan.Groups.Length, plan.Assignments.Length, journal.Read().Count, plan.Groups, plan.Warnings);

    private void RestoreEntries(HashSet<Guid> retain, CancellationToken ct)
    {
        using var suppression = GroupingPersistence.Suppress();
        var conflicts = new List<Guid>();
        var retired = new List<Guid>();
        var batch = new List<(BaseItem Item, KeyState Before)>();
        foreach (var entry in journal.Read().OrderBy(e => e.Original.Kind == "Series" ? 0 : 1))
        {
            ct.ThrowIfCancellationRequested();
            if (retain.Contains(entry.Original.Id)) continue;
            var item = library.GetItemById(entry.Original.Id);
            if (item is null) { retired.Add(entry.Original.Id); continue; }
            var current = GroupingPersistence.ReadKeys(item);
            if (current.Path != entry.Original.Path || current.Kind != entry.Original.Kind
                || (current.PresentationKey != entry.AppliedPresentationKey && current.PresentationKey != entry.Original.PresentationKey)
                || (current.SeriesKey != entry.AppliedSeriesKey && current.SeriesKey != entry.Original.SeriesKey))
            {
                conflicts.Add(item.Id);
                continue;
            }
            GroupingPersistence.Restore(item, entry.Original);
            batch.Add((item, current));
            if (batch.Count == 100) Flush();
        }
        Flush();
        // One journal replacement after native saves. If interrupted earlier, already-restored keys
        // still match Original and are safe to retry on the next run.
        if (retired.Count > 0) journal.Remove(retired);
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"Recovery retained {conflicts.Count} externally changed items rather than overwrite them: {string.Join(", ", conflicts.Take(10))}");

        void Flush()
        {
            if (batch.Count == 0) return;
            try { persistence.SaveItems(batch.Select(b => b.Item).ToArray(), ct); }
            catch { foreach (var change in batch) GroupingPersistence.Restore(change.Item, change.Before); throw; }
            retired.AddRange(batch.Select(b => b.Item.Id));
            batch.Clear();
        }
    }
}
