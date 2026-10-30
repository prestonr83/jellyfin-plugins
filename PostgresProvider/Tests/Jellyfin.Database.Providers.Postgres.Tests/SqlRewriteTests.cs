using System.Data.Common;
using Jellyfin.Database.Providers.Postgres.Services;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Covers the statement rewriting the plugin applies to Jellyfin's migration SQL. These are pure unit
/// tests: no database involved, which means a community contributor can inspect exactly what changes.
/// </summary>
public sealed class SqlRewriteTests
{
    /// <summary>
    /// Jellyfin 12.1 cleans up JSON columns with SQLite's <c>json_remove</c>, which PostgreSQL does not
    /// have; the plugin translates it to a jsonb expression.
    /// </summary>
    [Fact]
    public void SqliteJsonCleanupIsTranslatedToPostgres()
    {
        var command = Rewrite(
            $"""UPDATE "BaseItems" SET "Data" = {Jellyfin121MigrationInterceptor.SqliteCleanup} WHERE json_valid("Data") = 1;""");

        Assert.DoesNotContain("json_remove", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("::jsonb - ARRAY[", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("IS JSON OBJECT", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running the rewrite twice must not change the statement again, otherwise a retried command would
    /// end up with a different (nested) statement.
    /// </summary>
    [Theory]
    [InlineData("""UPDATE "BaseItems" SET "Data" = json_remove("Data", '$.LinkedChildren', '$.ExtraIds', '$.SupportsExternalTransfer') WHERE json_valid("Data") = 1;""")]
    [InlineData("""UPDATE "BaseItems" SET "InheritedParentalRatingValue" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""")]
    [InlineData("""UPDATE "BaseItems" SET "InheritedParentalRatingSubValue" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""")]
    public void RewritingIsIdempotent(string sql)
    {
        var once = Rewrite(sql).CommandText;
        var twice = Rewrite(once).CommandText;

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// <c>MigrateRatingLevels</c> sets the inherited rating to NULL for every item without a rating. On a
    /// large library that rewrites the whole table and aborts the migration on the command timeout, so the
    /// statement is restricted to the rows that actually change.
    /// </summary>
    [Theory]
    [InlineData("InheritedParentalRatingValue")]
    [InlineData("InheritedParentalRatingSubValue")]
    public void RatingLevelsUpdateOnlyTouchesRowsThatChange(string column)
    {
        var command = Rewrite($$"""UPDATE "BaseItems" SET "{{column}}" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""");

        Assert.Contains($"\"{column}\" IS NOT NULL", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anything the plugin does not know about has to reach PostgreSQL untouched.
    /// </summary>
    [Theory]
    [InlineData("""UPDATE "UserData" SET "Played" = true WHERE "UserId" IS NULL;""")]
    [InlineData("""UPDATE "BaseItems" SET "Name" = 'x' WHERE "Id" = '00000000-0000-0000-0000-000000000001';""")]
    [InlineData("""DELETE FROM "LinkedChildren" WHERE "ParentId" IS NULL;""")]
    public void UnrelatedStatementsAreLeftUntouched(string sql)
    {
        Assert.Equal(sql, Rewrite(sql).CommandText);
    }

    [Theory]
    [InlineData("InheritedParentalRatingValue", "@score")]
    [InlineData("InheritedParentalRatingSubValue", "@subScore")]
    [InlineData("InheritedParentalRatingValue", "NULL")]
    public void ParameterizedRatingMigrationSkipsAlreadyCorrectRows(string column, string value)
    {
        var sql = $$"""UPDATE "BaseItems" AS b SET "{{column}}" = {{value}} WHERE b."OfficialRating" = @rating;""";
        var command = Rewrite(sql);
        Assert.Contains($"b.\"{column}\" IS DISTINCT FROM {value}", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(command.CommandText, Rewrite(command.CommandText).CommandText);
    }

    [Fact]
    public void RatingUpdatesWithAdditionalPredicatesAreNotRewritten()
    {
        const string sql = """UPDATE "BaseItems" AS b SET "InheritedParentalRatingValue" = @score WHERE b."OfficialRating" = @rating AND b."Type" = @type;""";
        Assert.Equal(sql, Rewrite(sql).CommandText);
    }

    private static DbCommand Rewrite(string sql)
    {
        var command = new NpgsqlCommand(sql);

        // The interceptor only reads and rewrites CommandText, so no connection or event data is needed.
        new Jellyfin121MigrationInterceptor().NonQueryExecuting(command, null!, default);
        return command;
    }

    /// <summary>
    /// A duplicate INSERT into <c>BaseItems</c> used to fail the entire batch with 23505 (channel items and
    /// partially created images). Ignoring it matches SQLite behavior and preserves the existing row.
    /// </summary>
    [Fact]
    public void DuplicateBaseItemInsertKeepsTheStoredRowAndReportsOneRow()
    {
        var command = RewriteUpsert("""INSERT INTO "BaseItems" ("Id", "Name") VALUES (@p0, @p1);""");

        // DO NOTHING would report zero affected rows and EF Core reports that as a concurrency failure,
        // so the stored row is kept through a no-op update instead.
        Assert.Contains("ON CONFLICT (\"Id\") DO UPDATE SET \"Id\" = EXCLUDED.\"Id\"", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("DO NOTHING", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// An on-demand Person refresh can lose the row between Jellyfin's existence read and its full entity
    /// update. The specific parameter-only update produced by ItemPersistenceService becomes an atomic
    /// upsert, so PostgreSQL can recreate the vanished item rather than reporting zero affected rows.
    /// </summary>
    [Fact]
    public void FullBaseItemUpdateBecomesAnAtomicUpsert()
    {
        var command = RewriteUpsert(
            """
            UPDATE "BaseItems" SET "Name" = @p0, "Overview" = @p1, "Type" = @p2
            WHERE "Id" = @p3;
            """);

        Assert.Contains(
            """INSERT INTO "BaseItems" ("Id", "Name", "Overview", "Type") VALUES (@p3, @p0, @p1, @p2) ON CONFLICT ("Id") DO UPDATE SET "Name" = @p0, "Overview" = @p1, "Type" = @p2;""",
            command.CommandText,
            StringComparison.Ordinal);
    }

    /// <summary>Code-migration updates must retain their original SQL semantics.</summary>
    [Fact]
    public void NonEntityBaseItemUpdatesAreLeftUntouched()
    {
        const string sql = """UPDATE "BaseItems" SET "Data" = json_remove("Data", '$.ExtraIds') WHERE "Id" = @p0;""";

        Assert.Equal(sql, RewriteUpsert(sql).CommandText);
    }

    /// <summary>Partial updates do not contain all required BaseItems columns and must stay updates.</summary>
    [Fact]
    public void PartialBaseItemUpdatesAreLeftUntouched()
    {
        const string sql = """UPDATE "BaseItems" SET "Name" = @p0 WHERE "Id" = @p1;""";

        Assert.Equal(sql, RewriteUpsert(sql).CommandText);
    }

    /// <summary>User playback updates must be atomic: concurrent saves update the existing row.</summary>
    [Fact]
    public void DuplicateUserDataInsertUpdatesTheExistingRow()
    {
        var command = RewriteUpsert(
            """INSERT INTO "UserData" ("ItemId", "UserId", "CustomDataKey", "Played", "PlaybackPositionTicks") VALUES (@p0, @p1, @p2, @p3, @p4);""");

        Assert.Contains("""ON CONFLICT ("ItemId", "UserId", "CustomDataKey") DO UPDATE SET""", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(""""Played" = EXCLUDED."Played"""", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(""""PlaybackPositionTicks" = EXCLUDED."PlaybackPositionTicks"""", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duplicate value must use the (Type, Value) pair as the conflict target: ItemValueId is
    /// a new GUID on every attempt, so an ON CONFLICT without a target would never trigger and the INSERT
    /// would keep failing with 23505.
    /// </summary>
    [Fact]
    public void DuplicateItemValueInsertNamesTheUniquePairAsConflictTarget()
    {
        var command = RewriteUpsert("""INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);""");

        Assert.Contains("ON CONFLICT (\"Type\", \"Value\") DO UPDATE SET \"Type\" = EXCLUDED.\"Type\"", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// If the value was not inserted because it already existed, the mapping INSERT would reference a
    /// missing row and violate FK_ItemValuesMap_ItemValues_ItemValueId, failing the entire batch
    /// (including its items). Therefore, insert a mapping only when the value actually exists.
    /// </summary>
    [Theory]
    [InlineData("""("ItemId", "ItemValueId") VALUES (@p4, @p0)""")]
    [InlineData("""("ItemValueId", "ItemId") VALUES (@p0, @p4)""")]
    public void MappingInsertBecomesConditionalWhenValuesTravelInTheSameBatch(string mapping)
    {
        var command = RewriteUpsert(
            $"""
            INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);
            INSERT INTO "ItemValuesMap" {mapping};
            """);

        Assert.Contains(
            """SELECT @p4, @p0 WHERE EXISTS (SELECT 1 FROM "ItemValues" AS iv WHERE iv."ItemValueId" = @p0)""",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains(
            """ON CONFLICT ("ItemValueId", "ItemId") DO UPDATE SET""",
            command.CommandText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Leave a standalone mapping unchanged: its value may come from a row already present
    /// in the database, in which case the foreign key is valid.
    /// </summary>
    [Fact]
    public void MappingInsertAloneKeepsItsShape()
    {
        var command = RewriteUpsert("""INSERT INTO "ItemValuesMap" ("ItemId", "ItemValueId") VALUES (@p0, @p1);""");

        Assert.DoesNotContain("EXISTS", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("""ON CONFLICT ("ItemValueId", "ItemId") DO UPDATE SET""", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// If a concurrent scan removes an item between Jellyfin's existence check and SaveChanges, dependent
    /// image and provider rows must be skipped rather than causing FK_BaseItemImageInfos_BaseItems_ItemId.
    /// The parent is present for ordinary inserts, so this leaves the normal path unchanged.
    /// </summary>
    [Theory]
    [InlineData("""INSERT INTO "BaseItemImageInfos" ("Id", "ItemId", "Path") VALUES (@p0, @p1, @p2);""", "BaseItemImageInfos", "@p1")]
    [InlineData("""INSERT INTO "BaseItemProviders" ("ItemId", "ProviderId", "ProviderValue") VALUES (@p0, @p1, @p2);""", "BaseItemProviders", "@p0")]
    public void DependentBaseItemInsertIsConditionalOnTheParentStillExisting(string sql, string table, string itemIdParameter)
    {
        var command = RewriteUpsert(sql);

        Assert.Contains($"INSERT INTO \"{table}\"", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            $"WHERE EXISTS (SELECT 1 FROM \"BaseItems\" WHERE \"Id\" = {itemIdParameter})",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Rewriting twice must not change the statement: otherwise a retried command would fail.</summary>
    [Fact]
    public void ItemValueRewriteIsIdempotent()
    {
        var once = RewriteUpsert(
            """
            INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);
            INSERT INTO "ItemValuesMap" ("ItemId", "ItemValueId") VALUES (@p4, @p0);
            """).CommandText;
        var twice = RewriteUpsert(once).CommandText;

        Assert.Equal(once, twice);
    }

    private static DbCommand RewriteUpsert(string sql)
    {
        var command = new NpgsqlCommand(sql);

        new UpsertConflictInterceptor().NonQueryExecuting(command, null!, default);
        return command;
    }
}
