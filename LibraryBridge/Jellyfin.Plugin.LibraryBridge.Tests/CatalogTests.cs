using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryBridge.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class CatalogTests
{
    [Fact] public void MovieLibrariesReadNestedContentsAcrossTheirPhysicalRoots()
    {
        var local = Movie("/movies/Example/movie.mkv", "123");
        var demand = Movie("/demand/Example.strm", "123");
        var catalog = Create("Movie", [local], [demand]);

        var plan = catalog.Build(Configuration("Movie"), CancellationToken.None);

        Assert.Equal(new[] { local.Id, demand.Id }, Assert.Single(plan.Groups).ItemIds);
        Assert.Equal(2, plan.Assignments.Length);
        Assert.Empty(plan.Warnings);
    }

    [Fact] public void SeriesLibrariesIncludeSeasonsAndEpisodesBelowThePhysicalRoots()
    {
        var local = Show("/shows/Example", "321");
        var demand = Show("/demand/Example", "321");
        var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", Path = local.Path + "/Season 1", SeriesId = local.Id, IndexNumber = 1 };
        var episode = Episode(local, 1);
        var other = Episode(demand, 1);
        var unique = Episode(demand, 2);
        var catalog = Create("Series", [local, season, episode], [demand, other, unique]);

        var plan = catalog.Build(Configuration("Series"), CancellationToken.None);

        Assert.Equal(2, plan.Groups.Length);
        Assert.Equal(6, plan.Assignments.Length);
        var key = plan.Groups.Single(g => g.Kind == "Series").Key;
        Assert.All(plan.Assignments.Where(a => a.Kind is "Season" or "Episode"), a => Assert.Equal(key, a.SeriesKey));
        Assert.Null(plan.Assignments.Single(a => a.Id == unique.Id).PresentationKey);
        Assert.Empty(plan.Warnings);
    }

    [Fact] public void EmptyLibraryReportsWhichSideHasNoScannedItems()
    {
        var plan = Create("Movie", [Movie("/movies/example.mkv", "123")], [])
            .Build(Configuration("Movie"), CancellationToken.None);

        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Warnings, message => message.Contains("Alternate") && message.Contains("no scanned"));
    }

    [Fact] public void NoMatchesReportsTitleCountsAndTheProviderIdentityRequirement()
    {
        var plan = Create("Movie", [Movie("/movies/example.mkv", "123")], [Movie("/demand/example.strm", "456")])
            .Build(Configuration("Movie"), CancellationToken.None);

        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Warnings, message => message.Contains("Primary: 1") && message.Contains("Alternate: 1") && message.Contains("provider IDs"));
    }

    [Fact] public void LargeLibrariesHydrateAllIdsInBoundedBatchesWithoutImageOrHistoryJoins()
    {
        var local = Enumerable.Range(1, 301).Select(number => Movie($"/movies/{number}.mkv", number.ToString())).ToArray();
        var demand = Enumerable.Range(1, 301).Select(number => Movie($"/demand/{number}.strm", number.ToString())).ToArray();
        var batches = new List<int>();

        var plan = Create("Movie", local, demand, batches).Build(Configuration("Movie"), CancellationToken.None);

        Assert.Equal(301, plan.Groups.Length);
        Assert.Equal(602, plan.Assignments.Length);
        Assert.Equal(602, batches.Sum());
        Assert.All(batches, count => Assert.InRange(count, 1, 128));
        Assert.Equal(6, batches.Count);
        Assert.Empty(plan.Warnings);
    }

    [Fact] public void MissingPhysicalRootsNeverBecomeAnUnscopedWholeCatalogQuery()
    {
        var plan = Create("Movie", [Movie("/movies/example.mkv", "123")], [Movie("/demand/example.strm", "123")], missingPrimaryRoots: true)
            .Build(Configuration("Movie"), CancellationToken.None);

        Assert.Empty(plan.Groups);
        Assert.Contains(plan.Warnings, message => message.Contains("Primary") && message.Contains("no scanned"));
    }

    private static Movie Movie(string path, string tmdb) => new()
    {
        Id = Guid.NewGuid(), Name = "Example", Path = path,
        ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = tmdb }
    };
    private static Series Show(string path, string tvdb) => new()
    {
        Id = Guid.NewGuid(), Name = "Example", Path = path,
        ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = tvdb }
    };
    private static Episode Episode(Series series, int number) => new()
    {
        Id = Guid.NewGuid(), Name = "Episode " + number, Path = series.Path + $"/S01E{number:00}.strm",
        SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = number
    };
    private static PluginConfiguration Configuration(string kind) => new()
    {
        LibraryPairs = [new() { PrimaryLibrary = "Primary", AlternateLibrary = "Alternate", ContentType = kind }]
    };

    private static Catalog Create(string kind, BaseItem[] local, BaseItem[] demand, List<int>? batchSizes = null, bool missingPrimaryRoots = false)
    {
        var primary = new CollectionFolder { Id = Guid.NewGuid(), Name = "Primary", Path = "/config/root/Primary",
            PhysicalFolderIds = missingPrimaryRoots ? [] : [Guid.NewGuid(), Guid.NewGuid()] };
        var alternate = new CollectionFolder { Id = Guid.NewGuid(), Name = "Alternate", Path = "/config/root/Alternate",
            PhysicalFolderIds = [Guid.NewGuid(), Guid.NewGuid()] };
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = primary.Name, ItemId = primary.Id.ToString("N") },
            new() { Name = alternate.Name, ItemId = alternate.Id.ToString("N") }
        };
        var library = StubProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetVirtualFolders") return folders;
            if (method.Name == "GetItemById")
            {
                var id = (Guid)args![0]!;
                return id == primary.Id ? primary : id == alternate.Id ? alternate
                    : local.Concat(demand).SingleOrDefault(item => item.Id == id);
            }
            if (method.Name is "GetItemIds" or "GetItemList")
            {
                var query = Assert.IsType<InternalItemsQuery>(args![0]);
                Assert.Equal(Guid.Empty, query.ParentId);
                Assert.NotEmpty(query.TopParentIds);
                var fromPrimary = query.TopParentIds.SequenceEqual(primary.PhysicalFolderIds);
                Assert.Equal(fromPrimary ? primary.PhysicalFolderIds : alternate.PhysicalFolderIds, query.TopParentIds);
                var available = fromPrimary ? local : demand;
                Assert.False(query.GroupByPresentationUniqueKey);
                Assert.True(query.IncludeAlternateVersions);
                Assert.False(query.IsVirtualItem);
                Assert.Equal(new[] { SourceType.Library }, query.SourceTypes);
                Assert.Equal(kind == "Movie" ? new[] { BaseItemKind.Movie }
                    : new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode }, query.IncludeItemTypes);
                if (method.Name == "GetItemIds")
                {
                    Assert.True(query.Recursive);
                    return available.Select(item => item.Id).ToArray();
                }
                Assert.InRange(query.ItemIds.Length, 1, 128);
                Assert.True(query.DtoOptions.ContainsField(ItemFields.ProviderIds));
                Assert.False(query.DtoOptions.EnableImages);
                Assert.False(query.DtoOptions.EnableUserData);
                batchSizes?.Add(query.ItemIds.Length);
                return available.Where(item => query.ItemIds.Contains(item.Id)).ToArray();
            }
            throw new InvalidOperationException("Unexpected library call: " + method.Name);
        });
        return new Catalog(library);
    }
}
