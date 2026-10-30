using System.Data.Common;
using System.Diagnostics;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Exercises large provider collections with the server model and the real read interceptor.</summary>
public sealed class CollectionQueryTests(ITestOutputHelper output)
{
    private const string SkipReason = "POSTGRES_TEST_CONNECTION is not set; skipping PostgreSQL integration tests.";
    private const string CollectionType = "MediaBrowser.Controller.Entities.Movies.BoxSet";
    private const int CollectionCount = 4;
    private const int LinksPerCollection = 2000;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeCollectionsRetainMembershipImagesAndProviders(bool asynchronous)
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        await SeedAsync(scratch.Database.ConnectionString);

        var observed = new CommandObserver();
        using var context = CreateContext(scratch.Database.ConnectionString, observed);
        var query = Collections(context);
        var items = asynchronous ? await query.ToListAsync() : query.ToList();

        Assert.Equal(CollectionCount, items.Count);
        Assert.All(items, AssertCompleteCollection);
        Assert.Equal(4, observed.Commands.Count);
    }

    [SkippableFact]
    public async Task CollectionPagesWithEqualNamesRemainCompleteAndDisjoint()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        await SeedAsync(scratch.Database.ConnectionString);

        using var context = CreateContext(scratch.Database.ConnectionString);
        var first = await Collections(context).Take(2).ToListAsync();
        var second = await Collections(context).Skip(2).Take(2).ToListAsync();
        var expected = await context.BaseItems.Where(item => item.Type == CollectionType)
            .OrderBy(item => item.SortName).ThenBy(item => item.Id).Select(item => item.Id).ToListAsync();

        Assert.Equal(expected, first.Concat(second).Select(item => item.Id));
        Assert.All(first.Concat(second), AssertCompleteCollection);
    }

    [SkippableFact]
    public async Task SynchronousStartupReadCanStillUpdateOnTheSameConnection()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        await SeedAsync(scratch.Database.ConnectionString);

        using var context = CreateContext(scratch.Database.ConnectionString);
        var expected = await context.BaseItems.CountAsync();
        foreach (var name in context.BaseItems.Select(item => item.Name).Distinct().AsEnumerable())
        {
            // MigrateRatingLevels enumerates a read and updates through the same context. The cache's
            // synchronous buffering must still close the Npgsql reader before this command executes.
            context.BaseItems.Where(item => item.Name == name)
                .ExecuteUpdate(update => update.SetProperty(item => item.InheritedParentalRatingValue, 7));
        }

        Assert.Equal(expected,
            await context.BaseItems.CountAsync(item => item.InheritedParentalRatingValue == 7));
    }

    [SkippableFact]
    public async Task SplitAndJoinedQueriesReturnTheSameCollectionsWithSmallerBuffers()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        await SeedAsync(scratch.Database.ConnectionString);

        var single = await MeasureAsync(scratch.Database.ConnectionString, forceSingleQuery: true);
        var split = await MeasureAsync(scratch.Database.ConnectionString, forceSingleQuery: false);

        Assert.Equal(single.Items.Select(item => item.Id), split.Items.Select(item => item.Id));
        Assert.All(single.Items, AssertCompleteCollection);
        Assert.All(split.Items, AssertCompleteCollection);
        Assert.Equal(1, single.Commands);
        Assert.Equal(4, split.Commands);
        output.WriteLine($"Joined: {single.Elapsed.TotalMilliseconds:F0} ms, {single.AllocatedBytes / 1048576d:F1} MiB allocated, {single.Commands} command.");
        output.WriteLine($"Split: {split.Elapsed.TotalMilliseconds:F0} ms, {split.AllocatedBytes / 1048576d:F1} MiB allocated, {split.Commands} commands.");
        // Allocation/time totals are diagnostic measurements, not timing-sensitive pass conditions.
    }

    private static IQueryable<BaseItemEntity> Collections(JellyfinDbContext context)
        // BaseItemRepository.PrepareItemQuery explicitly forces single-query mode before applying
        // its filters and navigation includes. A provider default alone cannot override that call.
        => context.BaseItems.AsNoTracking().AsSingleQuery().Where(item => item.Type == CollectionType).Distinct()
            .Include(item => item.LinkedChildEntities)
            .Include(item => item.Images)
            .Include(item => item.Provider)
            .OrderBy(item => item.SortName).ThenBy(item => item.Id);

    private static void AssertCompleteCollection(BaseItemEntity item)
    {
        Assert.NotNull(item.LinkedChildEntities);
        Assert.NotNull(item.Images);
        Assert.NotNull(item.Provider);
        Assert.Equal(LinksPerCollection, item.LinkedChildEntities.Count);
        Assert.Equal(LinksPerCollection, item.LinkedChildEntities.Select(link => link.ChildId).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, LinksPerCollection), item.LinkedChildEntities.Select(link => link.SortOrder).Order());
        Assert.Equal(2, item.Images.Count);
        Assert.Equal(2, item.Provider.Count);
        Assert.All(item.Images, image => Assert.StartsWith("/fixture/", image.Path));
        Assert.All(item.Provider, provider => Assert.False(string.IsNullOrWhiteSpace(provider.ProviderValue)));
        Assert.Contains("payload", item.Data, StringComparison.Ordinal);
    }

    private static async Task<Measurement> MeasureAsync(string connectionString, bool forceSingleQuery)
    {
        var observed = new CommandObserver();
        using var context = CreateContext(connectionString, observed, forceSingleQuery);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var items = await Collections(context).ToListAsync();
        timer.Stop();
        return new Measurement(items, timer.Elapsed, GC.GetTotalAllocatedBytes(precise: true) - before, observed.Commands.Count);
    }

    private static JellyfinDbContext CreateContext(string connectionString, CommandObserver? observer = null, bool forceSingleQuery = false)
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseNpgsql(connectionString, pg =>
            {
                PostgresDatabaseProvider.ConfigureNpgsql(pg, commandTimeout: 90);
                if (forceSingleQuery)
                {
                    pg.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                }
            })
            .AddInterceptors(new HomeQueryCacheInterceptor());
        if (!forceSingleQuery)
        {
            builder.AddInterceptors(new PostgresItemQuerySplittingInterceptor());
        }

        if (observer is not null)
        {
            builder.AddInterceptors(observer);
        }

        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null), new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task SeedAsync(string connectionString)
    {
        using (var context = TestEnvironment.CreateModelContext(connectionString))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO "BaseItems" ("Id", "Type", "IsMovie", "IsLocked", "IsSeries", "IsRepeat", "IsInMixedFolder", "IsFolder", "IsVirtualItem", "Name", "SortName", "Data")
            SELECT md5('collection-' || value::text)::uuid, '{{CollectionType}}', false, false, false, false, false, true, false, 'Same name', 'same name', json_build_object('payload', repeat('x', 2048))::text
            FROM generate_series(1, {{CollectionCount}}) AS value;
            INSERT INTO "BaseItems" ("Id", "Type", "IsMovie", "IsLocked", "IsSeries", "IsRepeat", "IsInMixedFolder", "IsFolder", "IsVirtualItem", "Name")
            SELECT md5('movie-' || value::text)::uuid, 'Movie', true, false, false, false, false, false, false, 'Movie'
            FROM generate_series(1, {{LinksPerCollection}}) AS value;
            INSERT INTO "LinkedChildren" ("ParentId", "SortOrder", "ChildId", "ChildType")
            SELECT md5('collection-' || parent::text)::uuid, child - 1, md5('movie-' || child::text)::uuid, 0
            FROM generate_series(1, {{CollectionCount}}) AS parent CROSS JOIN generate_series(1, {{LinksPerCollection}}) AS child;
            INSERT INTO "BaseItemProviders" ("ItemId", "ProviderId", "ProviderValue")
            SELECT md5('collection-' || parent::text)::uuid, provider, parent::text
            FROM generate_series(1, {{CollectionCount}}) AS parent CROSS JOIN (VALUES ('Tmdb'), ('Imdb')) AS ids(provider);
            INSERT INTO "BaseItemImageInfos" ("Id", "ItemId", "Path", "Height", "Width", "ImageType")
            SELECT md5('image-' || parent::text || '-' || image::text)::uuid, md5('collection-' || parent::text)::uuid, '/fixture/' || parent::text || '-' || image::text || '.jpg', 100, 100, image
            FROM generate_series(1, {{CollectionCount}}) AS parent CROSS JOIN generate_series(0, 1) AS image;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Measurement(List<BaseItemEntity> Items, TimeSpan Elapsed, long AllocatedBytes, int Commands);

    private sealed class CommandObserver : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
