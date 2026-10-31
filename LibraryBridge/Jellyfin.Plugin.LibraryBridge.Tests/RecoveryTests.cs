using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LibraryBridgeTests-" + Guid.NewGuid());
    private string JournalPath => Path.Combine(_directory, "recovery.json");
    private static Movie Movie() => new() { Id = Guid.NewGuid(), Name = "Movie", Path = "/media/movie.mkv", PresentationUniqueKey = "original" };
    private static KeyAssignment Assignment(BaseItem item, string key = "librarybridge:group", string? series = null) =>
        new(item.Id, item.Path, GroupingPersistence.Kind(item), key, series, "Local", 0);
    private static BridgeIndex Index(params KeyAssignment[] assignments)
    {
        var index = new BridgeIndex(); index.Publish(new(assignments, [], [])); return index;
    }
    [Fact] public void ReapplyingAnExistingPlanDoesNotRewriteMetadataOrItsRecoveryJournal()
    {
        var item = Movie(); var assignment = Assignment(item);
        var plan = new GroupingPlan([assignment], [], []);
        var journal = new RecoveryJournal(JournalPath);
        var index = Index(assignment);
        var library = StubProxy.Create<ILibraryManager>((method, _) => method.Name == "GetItemById" ? item : null);
        var firstNative = new MemoryPersistence();
        GroupingPersistence.SavePlan(new GroupingPersistence(firstNative, index, journal, () => true), library, plan, default);
        Assert.Equal(1, firstNative.Saves);
        Assert.Equal(assignment.PresentationKey, item.PresentationUniqueKey);
        var originalJournal = File.ReadAllText(JournalPath);

        var readOnlyNative = new MemoryPersistence { OnSave = () => throw new InvalidOperationException("An unchanged plan must not write metadata.") };
        GroupingPersistence.SavePlan(new GroupingPersistence(readOnlyNative, index, journal, () => true), library, plan, default);

        Assert.Equal(0, readOnlyNative.Saves);
        Assert.Equal(originalJournal, File.ReadAllText(JournalPath));
        Assert.Equal("original", Assert.Single(journal.Read()).Original.PresentationKey);
    }
    [Fact] public void JournalSurvivesRestartAndRetainsFirstOriginal()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        journal.Capture([(GroupingPersistence.ReadKeys(item), Assignment(item))]);
        item.PresentationUniqueKey = "native-refresh";
        journal.Capture([(GroupingPersistence.ReadKeys(item), Assignment(item, "librarybridge:new"))]);
        var restored = Assert.Single(new RecoveryJournal(JournalPath).Read());
        Assert.Equal("original", restored.Original.PresentationKey);
        Assert.Equal("librarybridge:new", restored.AppliedPresentationKey);
    }
    [Fact] public void CorruptJournalFailsClosed()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(JournalPath, "broken");
        Assert.Throws<InvalidDataException>(() => new RecoveryJournal(JournalPath).Read());
    }
    [Fact] public void UnknownJournalVersionIsRejected()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(JournalPath, "{\"SchemaVersion\":999,\"Entries\":[]}");
        Assert.Throws<InvalidDataException>(() => new RecoveryJournal(JournalPath).Read());
    }
    [Fact] public void OriginalKeysAreDurableBeforeNativeSave()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        var native = new MemoryPersistence { OnSave = () => Assert.Equal("original", Assert.Single(new RecoveryJournal(JournalPath).Read()).Original.PresentationKey) };
        new GroupingPersistence(native, Index(Assignment(item)), journal, () => true).SaveItems([item], default);
        Assert.Equal("librarybridge:group", item.PresentationUniqueKey); Assert.Equal(1, native.Saves);
    }
    [Fact] public void LargeApplyCanPrepareRecoveryOnceBeforeSavingAnyBatch()
    {
        var items = Enumerable.Range(0, 200).Select(_ => Movie()).ToArray();
        var journal = new RecoveryJournal(JournalPath);
        var native = new MemoryPersistence { OnSave = () => Assert.Equal(200, new RecoveryJournal(JournalPath).Read().Count) };
        var writer = new GroupingPersistence(native, Index(items.Select(i => Assignment(i)).ToArray()), journal, () => true);
        writer.Prepare(items, default);
        var lastWrite = File.GetLastWriteTimeUtc(JournalPath);
        Assert.All(items, item => Assert.Equal("original", item.PresentationUniqueKey));
        foreach (var batch in items.Chunk(20)) writer.SaveItems(batch, default);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(JournalPath));
        Assert.All(items, item => Assert.Equal("librarybridge:group", item.PresentationUniqueKey));
    }
    [Fact] public void FailedNativeSaveRestoresMemoryAndKeepsRecovery()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        var native = new MemoryPersistence { OnSave = () => throw new IOException("disk") };
        Assert.Throws<IOException>(() => new GroupingPersistence(native, Index(Assignment(item)), journal, () => true).SaveItems([item], default));
        Assert.Equal("original", item.PresentationUniqueKey); Assert.Single(journal.Read());
    }
    [Fact] public void FailedJournalPreventsNativeWrite()
    {
        Directory.CreateDirectory(JournalPath); var item = Movie(); var native = new MemoryPersistence();
        var error = Record.Exception(() => new GroupingPersistence(native, Index(Assignment(item)), new(JournalPath), () => true).SaveItems([item], default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(0, native.Saves); Assert.Equal("original", item.PresentationUniqueKey);
    }
    [Fact] public void MetadataRefreshGetsTheSameGroupingWithoutReplacingOriginalKeys()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        var writer = new GroupingPersistence(new MemoryPersistence(), Index(Assignment(item)), journal, () => true);
        writer.SaveItems([item], default); item.PresentationUniqueKey = "refreshed"; writer.SaveItems([item], default);
        Assert.Equal("librarybridge:group", item.PresentationUniqueKey);
        Assert.Equal("original", Assert.Single(journal.Read()).Original.PresentationKey);
    }
    [Fact] public void SuppressionAllowsRollbackThroughTheSamePersistenceService()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        var writer = new GroupingPersistence(new MemoryPersistence(), Index(Assignment(item)), journal, () => true);
        writer.SaveItems([item], default);
        using (GroupingPersistence.Suppress())
        {
            GroupingPersistence.Restore(item, Assert.Single(journal.Read()).Original);
            writer.SaveItems([item], default);
        }
        Assert.Equal("original", item.PresentationUniqueKey);
    }
    [Fact] public void DisabledPluginDoesNotCreateRecoveryOrChangeKeys()
    {
        var item = Movie(); var journal = new RecoveryJournal(JournalPath);
        new GroupingPersistence(new MemoryPersistence(), Index(Assignment(item)), journal, () => false).SaveItems([item], default);
        Assert.Equal("original", item.PresentationUniqueKey); Assert.False(File.Exists(JournalPath));
    }
    [Fact] public void ChangedPathIsNotGroupedWithAnOldSnapshot()
    {
        var item = Movie(); var assignment = Assignment(item); item.Path = "/different.mkv";
        new GroupingPersistence(new MemoryPersistence(), Index(assignment), new(JournalPath), () => true).SaveItems([item], default);
        Assert.Equal("original", item.PresentationUniqueKey);
    }
    [Fact] public void ReidentifiedTitlesCannotKeepAnOldGroupOrExposeItsSources()
    {
        var item = Movie(); item.ProviderIds["Tmdb"] = "1";
        var catalog = new CatalogItem(item.Id, "Movie", item.Name, item.Path, Guid.NewGuid(), "Local", 0, item.ProviderIds);
        var assignment = Assignment(item) with { Signature = PlanBuilder.Signature(catalog) };
        Assert.True(GroupingPersistence.Matches(item, assignment));
        item.ProviderIds["Tmdb"] = "2";
        Assert.False(GroupingPersistence.Matches(item, assignment));
    }
    [Fact] public void ANewSeasonCreatedUnderABridgedSeriesGetsNativeRecoveryKeys()
    {
        var series = new Series { Id = Guid.NewGuid(), Path = "/shows/a", PresentationUniqueKey = "native-series" };
        var journal = new RecoveryJournal(JournalPath);
        journal.Capture([(GroupingPersistence.ReadKeys(series), Assignment(series))]);
        var season = new Season { Id = Guid.NewGuid(), Path = "/shows/a/s1", SeriesId = series.Id, IndexNumber = 1,
            PresentationUniqueKey = "librarybridge:group-001", SeriesPresentationUniqueKey = "librarybridge:group" };
        var assignment = Assignment(season, "librarybridge:group:s:1", "librarybridge:group");
        new GroupingPersistence(new MemoryPersistence(), Index(assignment), journal, () => true).SaveItems([season], default);
        var original = journal.Find(season.Id)!.Original;
        Assert.Equal("native-series", original.SeriesKey); Assert.Equal("native-series-001", original.PresentationKey);
    }
    [Fact] public void MissingJournalCannotAdoptUnknownBridgeKeys()
    {
        var item = Movie(); item.PresentationUniqueKey = "librarybridge:orphan";
        Assert.Throws<InvalidDataException>(() => new GroupingPersistence(new MemoryPersistence(), Index(Assignment(item)), new(JournalPath), () => true).SaveItems([item], default));
    }
    [Fact] public void CancellationStopsBeforeMutation()
    {
        var item = Movie(); var native = new MemoryPersistence();
        Assert.Throws<OperationCanceledException>(() => new GroupingPersistence(native, Index(Assignment(item)), new(JournalPath), () => true)
            .SaveItems([item], new CancellationToken(true)));
        Assert.Equal(0, native.Saves); Assert.Equal("original", item.PresentationUniqueKey);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

internal sealed class MemoryPersistence : IItemPersistenceService
{
    public int Saves { get; private set; }
    public Action? OnSave { get; init; }
    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) { OnSave?.Invoke(); Saves++; }
    public void DeleteItem(params IReadOnlyList<Guid> ids) { }
    public Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    public void UpdateInheritedValues() { }
}
