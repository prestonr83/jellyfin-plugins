# Jellyfin 12.1 / .NET 10

These historical notes describe provider **3.0.0.0** for Jellyfin **12.1.x**. For the current catalog build, use the [Jellyfin 12.4 notes](JELLYFIN-12.4.md) and [fork guide](../FORK.md).

## Compatibility

The 12.1 provider requires .NET SDK 10 to build, PostgreSQL **16 or later**, and
`pg_dump` / `psql` tools compatible with the server for upgrade backups.
Jellyfin 12.1 JSON cleanup uses the PostgreSQL 16+ `IS JSON` predicate.

| Dependency | Version |
| --- | --- |
| Jellyfin.Controller / Common / Database.Implementations | 12.1.0 |
| EF Core / Microsoft.Data.Sqlite | 10.0.11 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 |
| Framework / ABI | net10.0 / 12.1.0.0 |

Do not install this package on Jellyfin 10.11. The upstream catalog retains older
releases and their checksums; its workflow adds the new release with ABI 12.1.0.0.

## Upgrade

1. Keep a full backup of Jellyfin data and configuration and a PostgreSQL
   `pg_dump` before upgrading the server or provider.
2. Replace all three plugin DLLs together: `Jellyfin.Database.Providers.Postgres.dll`,
   `Npgsql.dll`, and `Npgsql.EntityFrameworkCore.PostgreSQL.dll`. Do not copy Jellyfin,
   EF Core, or ASP.NET DLLs from the build output into the plugins directory.
3. For an existing PostgreSQL database, Jellyfin applies the incremental
   `Jellyfin121` migration at startup. Backups requested by code migrations are saved
   in `{DataPath}/postgres-migration-backups`. Configure `PgBinPath` or add PostgreSQL
   tools to PATH before startup.
4. Before importing SQLite, upgrade and start that database with Jellyfin 12.1.
   The importer rejects old tables or columns before writing to PostgreSQL.
   It does not replace data migrations that the server must run.
5. After the first real startup, check login, libraries, playlists, playback
   progress, and logs.

The migration preserves the existing InitialCreate identifier, converts OwnerId and
PrimaryVersionId to UUID, adds LinkedChildren and current indexes, removes orphaned
permissions and preferences, and consolidates duplicate ItemValues while preserving
their associations. It does not confuse ExtraIds with OriginalLanguage. Imports and
exports copy only code migrations that were actually applied; pending migrations
remain pending.

The truncate option still replaces destination data. All tables are truncated before
the first row is copied to prevent data loss from later cascades. An import with
table errors is reported as failed.

To downgrade, restore the corresponding full backup. Reversing the schema migration
cannot recover dropped columns or duplicate playlists that the previous version
could not represent.

## Build and verify

```powershell
dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release
dotnet test Tests/Jellyfin.Database.Providers.Postgres.Tests -c Release
```

Without environment variables, checks do not open PostgreSQL connections.
For integration testing, provide a **dedicated test instance** with permission to
create databases. The suite creates new databases prefixed `jellyfin_provider_test_`:

```powershell
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=55439;Database=postgres;Username=postgres'
dotnet test Tests/Jellyfin.Database.Providers.Postgres.Tests -c Release
```

Tests cover the initial schema, upgrades with existing data, UUIDs, duplicates and
associations, orphaned permissions, synchronous and asynchronous JSON cleanup,
malformed JSON, imports and reimports into public and custom schemas, SQLite export,
applied migration history, rejection of old SQLite schemas, and backup/restore callbacks.
PostgreSQL and SQLite fixtures are retained for diagnosis. Do not use a production
instance for these tests.

`Resources/schema.sql` is a generated reference. The importer uses EF migrations;
it does not split or manually execute that file.

To regenerate SQL or migrations with dotnet-ef 10.0.11, include server dependencies
only during design time:

```powershell
dotnet build Jellyfin.Database.Providers.Postgres -c Release -p:IncludeJellyfinRuntime=true
dotnet ef migrations script --project Jellyfin.Database.Providers.Postgres --configuration Release --no-build -o Jellyfin.Database.Providers.Postgres/Resources/schema.sql
```

Build and publish again without `IncludeJellyfinRuntime` when distributing the plugin.
