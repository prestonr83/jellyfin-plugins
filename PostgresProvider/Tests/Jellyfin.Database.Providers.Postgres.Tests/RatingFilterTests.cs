using System.Linq.Expressions;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Rating filters must preserve direct, ancestor and linked matches without rescanning a union for each item.</summary>
public sealed class RatingFilterTests
{
    [Fact]
    public void RatingMembershipKeepsDistinctSetOperationsInSql()
    {
        using var context = CreateContext("Host=localhost;Database=unused;Username=unused", rewrite: true);
        var sql = Matches(context, ["18+", "PG-13"]).ToQueryString();
        Assert.Contains("UNION", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UNION ALL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryConcatenationAndOtherFiltersAreUnchanged()
    {
        using var context = TestEnvironment.CreateModelContext();
        var ratingIds = context.BaseItems.Where(item => item.OfficialRating == "G").Select(item => item.Id);
        var otherIds = context.BaseItems.Where(item => item.Name == "other").Select(item => item.Id);
        var interceptor = new PostgresRatingFilterInterceptor();
        var bag = ratingIds.Concat(otherIds).Expression;
        Assert.Same(bag, interceptor.QueryCompilationStarting(bag, null!));
        var unrelatedSet = otherIds.Concat(otherIds).Distinct().Expression;
        Assert.Same(unrelatedSet, interceptor.QueryCompilationStarting(unrelatedSet, null!));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewrittenRatingsPreserveMembershipCountsAndPages(bool asynchronous)
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, "POSTGRES_TEST_CONNECTION is not set.");
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        using (var seed = TestEnvironment.CreateModelContext(scratch.Database.ConnectionString))
        {
            await seed.Database.MigrateAsync();
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BaseItems" ("Id","Type","Name","OfficialRating","IsMovie","IsLocked","IsSeries","IsRepeat","IsInMixedFolder","IsFolder","IsVirtualItem")
                SELECT md5(value::text)::uuid,'Movie',value::text,CASE WHEN value IN (1,2) THEN 'G' WHEN value=5 THEN 'PG-13' END,false,false,false,false,false,value IN (2,3),false
                FROM generate_series(1,5) value;
                INSERT INTO "AncestorIds" ("ItemId","ParentItemId") VALUES (md5('1')::uuid,md5('2')::uuid);
                INSERT INTO "LinkedChildren" ("ParentId","SortOrder","ChildId","ChildType") VALUES
                (md5('2')::uuid,0,md5('1')::uuid,0),(md5('3')::uuid,0,md5('1')::uuid,0);
                """);
        }

        using var original = CreateContext(scratch.Database.ConnectionString, rewrite: false);
        using var updated = CreateContext(scratch.Database.ConnectionString, rewrite: true);
        foreach (var ratings in new string?[][] { ["G"], ["PG-13"], ["G", null], ["missing"] })
        {
            var expected = await Matches(original, ratings).OrderBy(item => item.Name).Select(item => item.Id).ToListAsync();
            var query = Matches(updated, ratings).OrderBy(item => item.Name).Select(item => item.Id);
            var actual = asynchronous ? await query.ToListAsync() : query.ToList();
            Assert.Equal(expected, actual);
            Assert.Equal(expected.Count, asynchronous ? await query.CountAsync() : query.Count());
            Assert.Equal(expected.Take(2), await query.Take(2).ToListAsync());
            Assert.Equal(expected.Skip(2), await query.Skip(2).ToListAsync());
        }

        Assert.Equal(3, await Matches(updated, ["G"]).CountAsync());
        Assert.Equal(1, await Matches(updated, ["PG-13"]).CountAsync());
    }

    private static IQueryable<BaseItemEntity> Matches(JellyfinDbContext context, string?[] ratings)
    {
        var direct = context.BaseItems.Where(item => ratings.Contains(item.OfficialRating)).Select(item => item.Id);
        var ancestors = context.AncestorIds.Where(item => direct.Contains(item.ItemId)).Select(item => item.ParentItemId);
        var links = context.LinkedChildren.Where(item => direct.Contains(item.ChildId)).Select(item => item.ParentId);
        var matches = direct.Concat(ancestors).Concat(links).Distinct();
        return context.BaseItems.Where(item => matches.Contains(item.Id));
    }

    private static JellyfinDbContext CreateContext(string connection, bool rewrite)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>().UseNpgsql(connection,
            pg => PostgresDatabaseProvider.ConfigureNpgsql(pg, 30));
        if (rewrite)
        {
            options.AddInterceptors(new PostgresRatingFilterInterceptor());
        }

        return new JellyfinDbContext(options.Options, NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null), new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
