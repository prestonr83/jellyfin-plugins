using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Grouped ID arrays must preserve filtered membership, count and paging.</summary>
public sealed class GroupedItemIdsTests
{
    private const string Subquery = """
        SELECT COALESCE(min(b0."Id") FILTER (WHERE b0."PrimaryVersionId" IS NULL), min(b0."Id"))
        FROM "BaseItems" AS b0
        WHERE b0."Type" = @type
        AND b0."Id" NOT IN (SELECT "ItemId" FROM "UserData" WHERE "UserId"=@user AND "Played")
        AND NOT EXISTS (SELECT 1 FROM "ItemValuesMap" m JOIN "ItemValues" v USING ("ItemValueId")
            WHERE m."ItemId"=b0."Id" AND v."Type"=4 AND v."CleanValue"=ANY(@blocked))
        GROUP BY b0."PresentationUniqueKey"
        """;

    private static readonly string Query = "SELECT b.\"Id\" FROM \"BaseItems\" AS b WHERE b.\"Id\" IN (" + Subquery + ") ORDER BY b.\"Name\", b.\"Id\"";

    [Fact]
    public void KnownGroupedMembershipIsRewrittenAndParametersAreRetained()
    {
        var rewritten = PostgresGroupedItemIdsInterceptor.Rewrite(Query);
        var source = Subquery.Replace("min(b0.\"Id\"))", "min(b0.\"Id\")) AS \"Id\"", StringComparison.Ordinal);
        Assert.Equal("WITH \"__jf_grouped_ids_0\" AS MATERIALIZED (" + source + ") "
            + Query.Replace(Subquery, "SELECT \"Id\" FROM \"__jf_grouped_ids_0\"", StringComparison.Ordinal), rewritten);
        Assert.Equal(rewritten, PostgresGroupedItemIdsInterceptor.Rewrite(rewritten));
    }

    [Fact]
    public void QuotesCommentsAndUnrelatedSqlAreNotMistakenForAGroupedRead()
    {
        foreach (var sql in new[]
        {
            "SELECT '" + Query.Replace("'", "''", StringComparison.Ordinal) + "'",
            "SELECT 1 /* " + Query + " */",
            "SELECT 1 -- " + Query,
            "SELECT $$" + Query + "$$",
            Query.Replace("GROUP BY b0.\"PresentationUniqueKey\"", "GROUP BY b0.\"Name\"", StringComparison.Ordinal),
            Query.Replace("SELECT COALESCE", "SELECT DISTINCT COALESCE", StringComparison.Ordinal),
            Query.Replace("WHERE b0.\"Type\"", "WHERE b0.\"ParentId\"=b.\"Id\" AND b0.\"Type\"", StringComparison.Ordinal),
            "DELETE FROM \"BaseItems\" AS b WHERE b.\"Id\" IN (" + Subquery + ")",
            Query + "; SELECT 1",
        })
        {
            Assert.Equal(sql, PostgresGroupedItemIdsInterceptor.Rewrite(sql));
        }
    }

    [Fact]
    public void MultipleGroupedSetsRemainBalanced()
    {
        var query = "SELECT b.\"Id\" FROM \"BaseItems\" AS b WHERE b.\"Id\" IN (" + Subquery + ") OR b.\"Id\" IN (" + Subquery + ")";
        var rewritten = PostgresGroupedItemIdsInterceptor.Rewrite(query);
        Assert.Equal(2, rewritten.Split(" AS MATERIALIZED", StringSplitOptions.None).Length - 1);
        Assert.Equal(rewritten.Count(character => character == '('), rewritten.Count(character => character == ')'));
    }

    [Fact]
    public void OnlyTheExactRewrittenCommandIsMarkedAsARead()
    {
        using var command = new NpgsqlCommand(Query);
        Assert.False(PostgresGroupedItemIdsInterceptor.IsRewrittenRead(command));
        new PostgresGroupedItemIdsInterceptor().ReaderExecuting(command, null!, default);
        Assert.True(PostgresGroupedItemIdsInterceptor.IsRewrittenRead(command));
        command.CommandText = "WITH changed AS (DELETE FROM \"BaseItems\" RETURNING *) SELECT * FROM changed";
        Assert.False(PostgresGroupedItemIdsInterceptor.IsRewrittenRead(command));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiltersDeduplicationCountAndPagesAreUnchanged(bool asynchronous)
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, "POSTGRES_TEST_CONNECTION is not set.");
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        using (var seed = TestEnvironment.CreateModelContext(scratch.Database.ConnectionString))
        {
            await seed.Database.MigrateAsync();
            seed.Users.Add(new User("grouped-ids-fixture", "fixture-auth", "fixture-reset")
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000099"),
            });
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BaseItems" ("Id","Type","Name","PresentationUniqueKey","PrimaryVersionId","IsMovie","IsLocked","IsSeries","IsRepeat","IsInMixedFolder","IsFolder","IsVirtualItem")
                SELECT ('10000000-0000-0000-0000-'||lpad(value::text,12,'0'))::uuid,
                    CASE WHEN value=5 THEN 'Episode' ELSE 'Movie' END,value::text,
                    CASE WHEN value IN(1,2) THEN 'same' WHEN value IN(6,7) THEN NULL ELSE value::text END,
                    CASE WHEN value=1 THEN '10000000-0000-0000-0000-000000000002'::uuid END,
                    false,false,false,false,false,false,false FROM generate_series(1,8) value;
                INSERT INTO "UserData" ("ItemId","UserId","CustomDataKey","Played","IsFavorite","PlayCount","PlaybackPositionTicks")
                VALUES ('10000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000099','fixture',true,false,1,0);
                INSERT INTO "ItemValues" ("ItemValueId","Type","Value","CleanValue")
                VALUES ('00000000-0000-0000-0000-000000000098',4,'Blocked','blocked');
                INSERT INTO "ItemValuesMap" ("ItemValueId","ItemId")
                VALUES ('00000000-0000-0000-0000-000000000098','10000000-0000-0000-0000-000000000004');
                """);
        }

        await using var connection = new NpgsqlConnection(scratch.Database.ConnectionString);
        await connection.OpenAsync();
        SqliteCompatibilityBootstrap.Ensure(connection);
        var original = await ReadIds(connection, Query, asynchronous);
        var updated = await ReadIds(connection, PostgresGroupedItemIdsInterceptor.Rewrite(Query), asynchronous);
        Assert.Equal(original, updated);
        var nested = Query.Replace("WHERE b0.\"Type\"", "WHERE b0.\"Id\" IN (" + Subquery.Replace("b0", "b9", StringComparison.Ordinal) + ") AND b0.\"Type\"", StringComparison.Ordinal);
        Assert.Equal(nested, PostgresGroupedItemIdsInterceptor.Rewrite(nested));
        Assert.Equal(original, await ReadIds(connection, PostgresGroupedItemIdsInterceptor.Rewrite(nested), asynchronous));
        Assert.Equal(new[] { 2, 6, 8 }, updated.Select(id => int.Parse(id.ToString("N")[^12..], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(original.Take(2), await ReadIds(connection, PostgresGroupedItemIdsInterceptor.Rewrite(Query + " LIMIT 2"), asynchronous));
        Assert.Equal(original.Skip(2), await ReadIds(connection, PostgresGroupedItemIdsInterceptor.Rewrite(Query + " OFFSET 2"), asynchronous));
        Assert.Empty(await ReadIds(connection, PostgresGroupedItemIdsInterceptor.Rewrite(Query), asynchronous, type: "missing"));

        await using var count = Command(connection, PostgresGroupedItemIdsInterceptor.Rewrite("SELECT count(*)::int FROM \"BaseItems\" AS b WHERE b.\"Id\" IN (" + Subquery + ")"));
        Assert.Equal(original.Count, asynchronous ? await count.ExecuteScalarAsync() : count.ExecuteScalar());

        await using var cached = Command(connection, Query);
        var rewriter = new PostgresGroupedItemIdsInterceptor();
        var cache = new HomeQueryCacheInterceptor();
        if (asynchronous)
        {
            await rewriter.ReaderExecutingAsync(cached, null!, default);
        }
        else
        {
            rewriter.ReaderExecuting(cached, null!, default);
        }

        var interception = asynchronous ? await cache.ReaderExecutingAsync(cached, null!, default) : cache.ReaderExecuting(cached, null!, default);
        Assert.True(interception.HasResult);
        await using var buffered = interception.Result;
        var bufferedIds = new List<Guid>();
        while (await buffered.ReadAsync())
        {
            bufferedIds.Add(buffered.GetGuid(0));
        }

        Assert.Equal(original, bufferedIds);
    }

    private static async Task<List<Guid>> ReadIds(NpgsqlConnection connection, string sql, bool asynchronous, string type = "Movie")
    {
        await using var command = Command(connection, sql, type);
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        var result = new List<Guid>();
        while (asynchronous ? await reader.ReadAsync() : reader.Read())
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, string type = "Movie")
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("user", Guid.Parse("00000000-0000-0000-0000-000000000099"));
        command.Parameters.AddWithValue("blocked", NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "blocked" });
        return command;
    }
}
