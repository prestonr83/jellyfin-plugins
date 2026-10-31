#if LIBRARYBRIDGE_NATIVE_TESTS
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.LibraryBridge.Core;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

/// <summary>Real Jellyfin repository and NextUpService against a fresh, disposable SQLite database.</summary>
public sealed class NativeQueryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<JellyfinDbContext> _options;
    private readonly BaseItemRepository _repository;
    private readonly NextUpService _nextUp;
    private readonly User _user = new("native-test", "auth", "reset");
    private readonly Guid _local = Guid.NewGuid(), _demand = Guid.NewGuid();
    private readonly IItemTypeLookup _types;
    public NativeQueryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using (var db = Context()) { db.Database.EnsureCreated(); db.Users.Add(_user); db.SaveChanges(); }
        var names = typeof(BaseItem).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(BaseItem).IsAssignableFrom(t))
            .Where(t => Enum.TryParse<BaseItemKind>(t.Name, out _)).GroupBy(t => Enum.Parse<BaseItemKind>(t.Name))
            .ToDictionary(g => g.Key, g => g.First().FullName!);
        _types = StubProxy.Create<IItemTypeLookup>((method, _) => method.Name == "get_BaseItemKindNames" ? names : Array.Empty<string>());
        var factory = StubProxy.Create<IDbContextFactory<JellyfinDbContext>>((method, _) =>
            method.Name == "CreateDbContextAsync" ? Task.FromResult(Context()) : Context());
        var app = StubProxy.Create<IServerApplicationHost>((method, args) => method.ReturnType == typeof(string) ? args?.FirstOrDefault() : null);
        var configuration = StubProxy.Create<IServerConfigurationManager>((method, _) => method.Name == "get_Configuration" ? new ServerConfiguration() : null);
        _repository = new(factory, app, _types, configuration, NullLogger<BaseItemRepository>.Instance);
        _nextUp = new(factory, _types, _repository);
    }
    private JellyfinDbContext Context() => new(_options, NullLogger<JellyfinDbContext>.Instance,
        StubProxy.Create<IJellyfinDatabaseProvider>((_, _) => null), new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    private InternalItemsQuery Query(params BaseItemKind[] kinds) => new(_user) { IncludeItemTypes = kinds, EnableTotalRecordCount = true };
    private BaseItemEntity Add(JellyfinDbContext db, BaseItemKind kind, Guid library, string name, string key)
    {
        var entity = new BaseItemEntity { Id = Guid.NewGuid(), Type = _types.BaseItemKindNames[kind], Name = name,
            CleanName = name.ToLowerInvariant(), SortName = name, TopParentId = library,
            PresentationUniqueKey = key, IsFolder = kind is BaseItemKind.Series or BaseItemKind.Season,
            IsMovie = kind == BaseItemKind.Movie, IsSeries = kind == BaseItemKind.Series };
        db.BaseItems.Add(entity); return entity;
    }
    [Fact] public void GlobalMovieSearchUsesOneResultAndAnExactTotal()
    {
        using (var db = Context()) { Add(db, BaseItemKind.Movie, _local, "Example", "shared"); Add(db, BaseItemKind.Movie, _demand, "Example", "shared"); db.SaveChanges(); }
        var query = Query(BaseItemKind.Movie); query.SearchTerm = "Example";
        var result = _repository.GetItems(query);
        Assert.Single(result.Items); Assert.Equal(1, result.TotalRecordCount);
    }
    [Fact] public void BothLibraryViewsKeepTheirOwnCopy()
    {
        Guid localId, demandId;
        using (var db = Context()) { localId = Add(db, BaseItemKind.Movie, _local, "Example", "shared").Id; demandId = Add(db, BaseItemKind.Movie, _demand, "Example", "shared").Id; db.SaveChanges(); }
        var local = Query(BaseItemKind.Movie); local.TopParentIds = [_local];
        var demand = Query(BaseItemKind.Movie); demand.TopParentIds = [_demand];
        Assert.Equal(localId, Assert.Single(_repository.GetItemIdsList(local)));
        Assert.Equal(demandId, Assert.Single(_repository.GetItemIdsList(demand)));
    }
    [Fact] public void DeduplicationHappensBeforePagination()
    {
        using (var db = Context())
        {
            foreach (var library in new[] { _local, _demand }) { Add(db, BaseItemKind.Movie, library, "Example A", "a"); Add(db, BaseItemKind.Movie, library, "Example B", "b"); }
            db.SaveChanges();
        }
        var query = Query(BaseItemKind.Movie); query.StartIndex = 1; query.Limit = 1;
        var result = _repository.GetItems(query);
        Assert.Single(result.Items); Assert.Equal(2, result.TotalRecordCount);
    }
    [Fact] public void NativeNextUpCollapsesLibrariesAndChoosesTheFollowingEpisode()
    {
        using (var db = Context())
        {
            foreach (var library in new[] { _local, _demand })
            {
                Add(db, BaseItemKind.Series, library, "Example", "series-key");
                for (var number = 1; number <= 2; number++)
                {
                    var episode = Add(db, BaseItemKind.Episode, library, "Episode " + number, "episode-" + number);
                    episode.SeriesPresentationUniqueKey = "series-key"; episode.ParentIndexNumber = 1; episode.IndexNumber = number;
                    db.UserData.Add(new UserData { ItemId = episode.Id, UserId = _user.Id, Item = null!, User = null!,
                        CustomDataKey = episode.Id.ToString("N"), Played = number == 1,
                        LastPlayedDate = number == 1 ? DateTime.UtcNow : null });
                }
            }
            db.SaveChanges();
        }
        var filter = Query(BaseItemKind.Episode); filter.TopParentIds = [_local, _demand];
        var key = Assert.Single(_nextUp.GetNextUpSeriesKeys(filter, DateTime.UtcNow.AddDays(-1)));
        var batch = _nextUp.GetNextUpEpisodesBatch(filter, [key], false, false);
        Assert.Single(batch); Assert.Equal(2, batch[key].NextUp?.IndexNumber);
    }
    [Fact] public void RestoringPresentationKeysMakesBothSearchResultsVisibleAgain()
    {
        using (var db = Context()) { Add(db, BaseItemKind.Movie, _local, "Example", "shared"); Add(db, BaseItemKind.Movie, _demand, "Example", "shared"); db.SaveChanges(); }
        Assert.Single(_repository.GetItemIdsList(Query(BaseItemKind.Movie)));
        using (var db = Context()) { foreach (var item in db.BaseItems) item.PresentationUniqueKey = item.Id.ToString("N"); db.SaveChanges(); }
        Assert.Equal(2, _repository.GetItemIdsList(Query(BaseItemKind.Movie)).Count);
    }
    private (BridgeIndex Index, Guid Local, Guid Demand, Guid Other) SeedResumes()
    {
        using var db = Context();
        var local = Add(db, BaseItemKind.Movie, _local, "Resumed A", "resume-a"); local.Path = "/local/a.mkv";
        var demand = Add(db, BaseItemKind.Movie, _demand, "Resumed A", "resume-a"); demand.Path = "/demand/a.strm";
        var other = Add(db, BaseItemKind.Movie, _local, "Resumed B", "resume-b"); other.Path = "/local/b.mkv";
        foreach (var item in new[] { local, demand })
            item.Provider = [new BaseItemProvider { Item = item, ItemId = item.Id, ProviderId = "Tmdb", ProviderValue = "945961" }];
        foreach (var item in new[] { local, demand, other })
            db.UserData.Add(new UserData { ItemId = item.Id, UserId = _user.Id, Item = null!, User = null!,
                CustomDataKey = item.Id.ToString("N"), PlaybackPositionTicks = 1000, LastPlayedDate = DateTime.UtcNow });
        db.SaveChanges();
        string Signature(BaseItemEntity entity) => PlanBuilder.Signature(new CatalogItem(entity.Id, "Movie", entity.Name!, entity.Path!,
            entity.TopParentId!.Value, "", 0, _repository.RetrieveItem(entity.Id)!.ProviderIds));
        var index = new BridgeIndex(); index.Publish(new([
            new(local.Id, local.Path, "Movie", "resume-a", null, "Local", 0, Signature(local)),
            new(demand.Id, demand.Path, "Movie", "resume-a", null, "On Demand", 1, Signature(demand))],
            [new("resume-a", "Movie", "Resumed A", [local.Id, demand.Id])], []));
        return (index, local.Id, demand.Id, other.Id);
    }
    [Fact] public void NativeResumePathIsDeduplicatedWithExactPageCounts()
    {
        var data = SeedResumes(); var query = Query(BaseItemKind.Movie); query.IsResumable = true;
        Assert.Equal(3, _repository.GetItemList(query).Count);
        var bridge = new ResumeRepository(_repository, data.Index, () => true);
        query.Limit = 1;
        var first = bridge.GetItems(query); query.StartIndex = 1; var second = bridge.GetItems(query);
        Assert.Single(first.Items); Assert.Single(second.Items);
        Assert.Equal(2, first.TotalRecordCount); Assert.Equal(2, second.TotalRecordCount);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }
    [Fact] public void NativeResumeAccessFilterKeepsTheCopyFromTheRequestedLibrary()
    {
        var data = SeedResumes(); var query = Query(BaseItemKind.Movie); query.IsResumable = true; query.TopParentIds = [_demand];
        var bridge = new ResumeRepository(_repository, data.Index, () => true);
        Assert.Equal(data.Demand, Assert.Single(bridge.GetItems(query).Items).Id);
    }
    [Fact] public void NativeResumeActiveSessionExclusionDoesNotLeaveTheAlternateVisible()
    {
        var data = SeedResumes(); var query = Query(BaseItemKind.Movie); query.IsResumable = true; query.ExcludeItemIds = [data.Local];
        var bridge = new ResumeRepository(_repository, data.Index, () => true);
        Assert.Equal(data.Other, Assert.Single(bridge.GetItems(query).Items).Id);
    }
    [Fact] public void ResumeEndpointWithoutRequestedProviderIdsStillCollapsesVerifiedCopies()
    {
        var data = SeedResumes(); var query = Query(BaseItemKind.Movie); query.IsResumable = true;
        // ItemsController replaces the default all-fields option with the fields requested by the client.
        query.DtoOptions.Fields = [];
        var ordinary = _repository.GetItemList(query);
        Assert.Equal(3, ordinary.Count);
        Assert.All(ordinary, item => Assert.Empty(item.ProviderIds));
        var bridge = new ResumeRepository(_repository, data.Index, () => true);

        var result = bridge.GetItems(query);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Empty(query.DtoOptions.Fields);
    }
    [Fact] public void LightweightResumeListsAndIdPagesUseLogicalOffsets()
    {
        var data = SeedResumes(); var query = Query(BaseItemKind.Movie); query.IsResumable = true;
        query.DtoOptions.Fields = []; query.Limit = 1;
        var bridge = new ResumeRepository(_repository, data.Index, () => true);
        var first = Assert.Single(bridge.GetItemIdsList(query));
        query.StartIndex = 1;
        var second = Assert.Single(bridge.GetItemList(query));

        Assert.NotEqual(first, second.Id);
        query.StartIndex = 2;
        Assert.Empty(bridge.GetItemIdsList(query));
    }
    [Fact] public void LightweightResumeQueryDoesNotHideAReidentifiedCopy()
    {
        var data = SeedResumes();
        using (var db = Context())
        {
            db.BaseItems.Include(item => item.Provider).Single(item => item.Id == data.Demand).Provider!.Single().ProviderValue = "123";
            db.SaveChanges();
        }
        var query = Query(BaseItemKind.Movie); query.IsResumable = true; query.DtoOptions.Fields = [];
        var bridge = new ResumeRepository(_repository, data.Index, () => true);

        Assert.Equal(3, bridge.GetItems(query).TotalRecordCount);
    }
    [Fact] public void EpisodeQueueStartMapsAnOmittedCopyToTheRealNativeGroupedRow()
    {
        Guid seriesId, startId, survivor;
        using (var db = Context())
        {
            var series = Add(db, BaseItemKind.Series, _local, "Show", "series"); series.Path = "/local/show"; seriesId = series.Id;
            var other = Add(db, BaseItemKind.Series, _demand, "Show", "series"); other.Path = "/demand/show";
            foreach (var library in new[] { _local, _demand })
            {
                var episode = Add(db, BaseItemKind.Episode, library, "Episode", "episode");
                episode.SeriesId = library == _local ? series.Id : other.Id;
                episode.SeriesPresentationUniqueKey = "series"; episode.IndexNumber = 3; episode.ParentIndexNumber = 17;
                episode.Path = library == _local ? "/local/show/episode.mkv" : "/demand/show/episode.strm";
            }
            db.SaveChanges();
        }
        var query = Query(BaseItemKind.Episode); query.SeriesPresentationUniqueKey = "series";
        var listing = _repository.GetItemIdsList(query);
        survivor = Assert.Single(listing);
        query.GroupByPresentationUniqueKey = false;
        startId = _repository.GetItemIdsList(query).Single(id => id != survivor);
        Assert.Empty(listing.SkipWhile(id => id != startId)); // The native endpoint's broken queue before normalization.

        var seriesQuery = Query(BaseItemKind.Series); seriesQuery.GroupByPresentationUniqueKey = false;
        var items = _repository.GetItemList(query).Concat(_repository.GetItemList(seriesQuery)).ToArray();
        var index = new BridgeIndex();
        index.Publish(new(items.Select(item => new KeyAssignment(item.Id, item.Path, GroupingPersistence.Kind(item),
            item.PresentationUniqueKey, item is MediaBrowser.Controller.Entities.TV.Episode ? "series" : null, "", 0)).ToArray(),
            [new("series", "Series", "Show", items.Where(item => item is MediaBrowser.Controller.Entities.TV.Series).Select(item => item.Id).ToArray()),
             new("episode", "Episode", "Episode", items.Where(item => item is MediaBrowser.Controller.Entities.TV.Episode).Select(item => item.Id).ToArray())], []));
        var libraryManager = StubProxy.Create<ILibraryManager>((method, args) => method.Name switch
        {
            "GetItemById" => items.SingleOrDefault(item => item.Id == (Guid)args![0]!),
            "GetItemIds" => _repository.GetItemIdsList((InternalItemsQuery)args![0]!).ToArray(),
            _ => null
        });
        var normalized = new PlaybackSources(libraryManager, index, () => true).EpisodeQueueStart(startId, seriesId, _user);
        Assert.Equal(survivor, normalized);
        Assert.Equal(survivor, Assert.Single(listing.SkipWhile(id => id != normalized)));
    }
    public void Dispose() => _connection.Dispose();
}
#endif
