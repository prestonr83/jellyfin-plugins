#if LIBRARYBRIDGE_NATIVE_TESTS
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Plugin.LibraryBridge.Configuration;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class NativePostgresCatalogTests
{
    [NativePostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedCatalogReadsLargeUnicodeMetadataWithThePostgresReaderAndOptionalCache(bool cacheEnabled)
    {
        var connection = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION")!;
        var database = "librarybridge_test_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(connection);
        admin.Open();
        using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) create.ExecuteNonQuery();
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false };
        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseNpgsql(builder.ConnectionString, postgres => postgres.EnableRetryOnFailure())
                .AddInterceptors(new PostgresItemQuerySplittingInterceptor());
            if (cacheEnabled) optionsBuilder.AddInterceptors(new HomeQueryCacheInterceptor());
            var options = optionsBuilder.Options;
            JellyfinDbContext Context() => new(options, NullLogger<JellyfinDbContext>.Instance,
                StubProxy.Create<IJellyfinDatabaseProvider>((_, _) => null), new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
            var primary = new CollectionFolder { Id = Guid.NewGuid(), Name = "Primary", PhysicalFolderIds = [Guid.NewGuid()] };
            var alternate = new CollectionFolder { Id = Guid.NewGuid(), Name = "Alternate", PhysicalFolderIds = [Guid.NewGuid()] };
            const int titleCount = 513;
            using (var db = Context())
            {
                db.Database.EnsureCreated();
                foreach (var root in new[] { primary, alternate })
                for (var number = 1; number <= titleCount; number++)
                {
                    var item = new BaseItemEntity
                    {
                        Id = Guid.NewGuid(), Type = "MediaBrowser.Controller.Entities.Movies.Movie",
                        Name = "Пример — 東京 🎬 " + number, SortName = "example-" + number,
                        Path = $"/test/{root.Name}/Пример-{number}.mkv", TopParentId = root.PhysicalFolderIds[0],
                        Overview = string.Concat(Enumerable.Repeat("Пример 東京 🎬 é ", 1024)),
                        IsMovie = true, PresentationUniqueKey = Guid.NewGuid().ToString("N")
                    };
                    item.Provider = [new BaseItemProvider { Item = item, ItemId = item.Id, ProviderId = "Tmdb", ProviderValue = number.ToString(System.Globalization.CultureInfo.InvariantCulture) }];
                    db.BaseItems.Add(item);
                }
                db.SaveChanges();
            }
            HomeQueryCacheInterceptor.Purge();
            var factory = StubProxy.Create<IDbContextFactory<JellyfinDbContext>>((method, _) =>
                method.Name == "CreateDbContextAsync" ? Task.FromResult(Context()) : Context());
            var names = new Dictionary<BaseItemKind, string> { [BaseItemKind.Movie] = "MediaBrowser.Controller.Entities.Movies.Movie" };
            var types = StubProxy.Create<IItemTypeLookup>((method, _) => method.Name == "get_BaseItemKindNames" ? names : Array.Empty<string>());
            var host = StubProxy.Create<IServerApplicationHost>((method, args) => method.ReturnType == typeof(string) ? args?.FirstOrDefault() : null);
            var config = StubProxy.Create<IServerConfigurationManager>((method, _) => method.Name == "get_Configuration" ? new ServerConfiguration() : null);
            var repository = new BaseItemRepository(factory, host, types, config, NullLogger<BaseItemRepository>.Instance);
            var batches = new List<int>();
            var library = StubProxy.Create<ILibraryManager>((method, args) => method.Name switch
            {
                "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>
                {
                    new() { Name = primary.Name, ItemId = primary.Id.ToString("N") },
                    new() { Name = alternate.Name, ItemId = alternate.Id.ToString("N") }
                },
                "GetItemById" => (Guid)args![0]! == primary.Id ? primary : alternate,
                "GetItemIds" => repository.GetItemIdsList((InternalItemsQuery)args![0]!),
                "GetItemList" => Read((InternalItemsQuery)args![0]!),
                _ => throw new InvalidOperationException("Unexpected library call: " + method.Name)
            });
            IReadOnlyList<BaseItem> Read(InternalItemsQuery query)
            {
                Assert.InRange(query.ItemIds.Length, 1, 128);
                batches.Add(query.ItemIds.Length);
                return repository.GetItemList(query);
            }
            var settings = new PluginConfiguration { LibraryPairs = [new() { PrimaryLibrary = "Primary", AlternateLibrary = "Alternate", ContentType = "Movie" }] };
            var first = new Catalog(library).Build(settings, CancellationToken.None);
            var second = new Catalog(library).Build(settings, CancellationToken.None);
            Assert.Equal(titleCount, first.Groups.Length);
            Assert.Equal(titleCount * 2, first.Assignments.Length);
            Assert.All(first.Groups, group => Assert.StartsWith("Пример — 東京 🎬 ", group.Name));
            Assert.Empty(first.Warnings);
            Assert.Equal(first.Groups.Select(group => group.Key).Order(), second.Groups.Select(group => group.Key).Order());
            Assert.Equal(titleCount * 4, batches.Sum());
        }
        finally
        {
            HomeQueryCacheInterceptor.Purge();
            using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            drop.ExecuteNonQuery();
        }
    }
}

public sealed class NativePostgresTheoryAttribute : TheoryAttribute
{
    public NativePostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION")))
            Skip = "Requires a disposable PostgreSQL server via POSTGRES_TEST_CONNECTION.";
    }
}
#endif
