# PostgreSQL provider in this catalog

This is the publishable copy of the existing modified provider, imported from the local working tree on 2026-10-30. It includes committed upstream files **and the previously uncommitted Jellyfin 12.4 compatibility/performance changes**. Future catalog-provider changes should be made here.

- Upstream: [BORNIOS/Jellyfin-Database-Providers-Postgres](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres)
- Imported upstream HEAD: `66d985302f5abb03c271e9d19f56080c572b934e`
- Prior local provider: `3.2.0.5`
- Catalog provider: `3.2.0.7`, Jellyfin `12.4.0.0`, .NET 10
- License: [GPL-3.0](LICENSE); upstream copyrights and attribution are preserved.

Version `3.2.0.7` makes the dashboard, confirmation dialogs, server messages, health findings, migration output, and documentation English. The dashboard uses one English message dictionary, including its initial HTML and accessibility labels. Plugin identity, configuration fields, API routes, database identifiers, and migrations retain their existing names and behavior.

The separate original checkout at `../Jellyfin-Database-Providers-Postgres/` has not been moved, reset, or had its Git history removed. It is ignored by the parent catalog repository. This imported directory is ordinary tracked source, so a fresh clone includes all the modifications without depending on an unpublished submodule commit. IDE settings and old upstream Actions/catalog files were omitted; the parent repository owns publishing.

## Install and update

Use the [parent catalog](../README.md#install-and-update-through-jellyfin), select **PostgreSQL Database Provider**, and restart when requested. The catalog package enables automatic updates and contains exactly:

- `Jellyfin.Database.Providers.Postgres.dll`
- `Npgsql.dll`
- `Npgsql.EntityFrameworkCore.PostgreSQL.dll`
- `meta.json`

The plugin GUID and assembly identity remain compatible with the existing installation and configuration. Because this retains the upstream plugin GUID, another enabled catalog can also offer updates for that same plugin. Use this catalog for the modified provider, and deliberately choose whether to retain a competing upstream provider catalog. An older local package may have automatic updates disabled; enable them in Jellyfin or install this newer version once through the Catalog.

Packaging through this repository changes distribution/version metadata and enables catalog updates; it does not activate PostgreSQL, change connection credentials, switch database engines, or run migrations against your live server. Existing PostgreSQL installations still need their existing database connection and client tools. See [the imported 12.4 notes](docs/JELLYFIN-12.4.md) for the runtime compatibility changes; historical package/version/update instructions there describe the prior local build.

## Validation

The shared release workflow runs the provider test suite against a disposable PostgreSQL 17 service before publishing. Ordinary local/Windows builds run its unit and SQLite tests; PostgreSQL integration tests explicitly skip unless `POSTGRES_TEST_CONNECTION` points at a disposable test instance with permission to create scratch databases. No production connection is selected automatically.

Backups and the optional live-schema/log probe retain their upstream opt-in environment variables. CI never points these tests at the running Jellyfin database.
