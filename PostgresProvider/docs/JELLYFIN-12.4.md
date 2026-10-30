# Jellyfin 12.4 compatibility build

This local branch is based on BORNIOS main commit
`66d985302f5abb03c271e9d19f56080c572b934e` and builds provider **3.2.0.5**.
It targets **Jellyfin 12.4.0 / ABI 12.4.0.0**, .NET 10,
EF Core / Microsoft.Data.Sqlite **10.0.14**, and Npgsql EF provider **10.0.2**.
PostgreSQL 17.11 was used for integration tests. PostgreSQL 16+ remains the
upstream minimum; this work did not run a PostgreSQL 16 compatibility matrix.

## Changes

- Materialize native grouped-ID membership reads as statement-local CTEs. This
  avoids nested-loop semi-joins whose tiny row estimates become thousands of
  actual filtered movie IDs. All selection, access, watched-state, version
  grouping, count and paging expressions remain intact. Correlated, nested or
  unknown SQL shapes remain unchanged. The result cache recognizes only the exact
  read commands marked by this rewriter, preserving buffering without enabling
  arbitrary WITH statements for caching.
- Preserve `UNION` for distinct rating-match ID sets before EF compilation.
  PostgreSQL can compute direct, ancestor and linked rating matches once instead
  of rescanning a parallel append for every tagged movie. Add a covering
  `(OfficialRating, Id)` index for these lookups. Membership, counts and paging
  retain the native filter semantics, including nullable ratings.
- Use split queries for item collection navigations. Provider collections with
  thousands of linked items no longer repeat the full collection metadata for
  every linked item, image and provider combination. Synchronous read buffering
  remains enabled for startup migrations that update while enumerating a read.
- Rewrite Jellyfin's explicit `AsSingleQuery<BaseItemEntity>` hints before query
  compilation. Its repository forces single-query mode on library and search
  reads, overriding a provider default. User/security queries retain their hints.
- Share the Jellyfin and EF versions between the plugin and test projects.
- Add migration `20261028220956_Jellyfin124` and a matching snapshot. It replaces
  `IX_BaseItems_OwnerId` and `IX_BaseItems_ExtraType_OwnerId` with the filtered
  definitions in the 12.4 model. It does not delete tables or columns, reset the
  database, or replace existing migration history.
- Implement Jellyfin's `RefreshStatistics` hook with `ANALYZE` on library tables.
  Shutdown remains a no-op; it does not perform maintenance while stopping.
- Package exactly the provider DLL, Npgsql DLL, Npgsql EF provider DLL, and
  `meta.json`. The manifest identifies ABI 12.4, disables automatic provider
  updates, and whitelists the plugin assembly for regular plugin discovery.
- Reuse the same packaging script in CI and releases. Release packaging is
  preceded by the PostgreSQL test suite.
- Restrict the rating migration's exact parameterized updates to values that
  actually change, including NULL-safe comparisons. This preserves the result
  while avoiding PostgreSQL heap/index rewrites for already-correct scores.

The provider migration is only part of a server upgrade. Jellyfin 12.4 also runs
its pending code migrations, including watched-state harmonization, encoder
preset repair and rating-level recalculation for the observed 12.1 history.
Keep those migrations pending so Jellyfin can execute them; do not forge new
history rows or suppress `PendingModelChangesWarning`.

## Build

With a .NET 10 SDK and PowerShell 7:

```powershell
./Tools/Build-Jellyfin124.ps1
```

The output is
`artifacts/Jellyfin.Database.Providers.Postgres-3.2.0.5-jellyfin12.4.zip`, its
SHA-256 file, and `artifacts/package-info.json`. The ZIP has four root entries.
`artifacts/publish/` is intermediate output; do not copy that whole directory to
Jellyfin. Do not add Jellyfin, EF Core, ASP.NET, `.deps.json` or `.runtimeconfig.json`
files to the plugin folder.

The ordinary test run skips real PostgreSQL tests unless
`POSTGRES_TEST_CONNECTION` is set. Point it only at a disposable test instance
whose login can create databases. Tests create `jellyfin_provider_test_*`
databases and remove them afterward. The optional `POSTGRES_LIVE_SCHEMA_PROBE`
also requires a real server log directory and is not part of the default run.

## Server image

The verified base is LinuxServer `12.4ubu2604-ls57`. The bare image has no
`pg_dump`; startup migrations can therefore fail before the server is usable.
The production deployment uses the existing universal-package-install mod with
`postgresql-client-17`, plus a read-only ConfigMap containing the signed PGDG
repository configuration and public key. See `k3s/jellyfin/upgrade-12.4.md` in
the parent workspace. `Dockerfile.jellyfin124` remains an alternative recipe;
this task did not publish a custom image.

Use `/usr/lib/postgresql/17/bin` as `PgBinPath`, or retain PATH-based detection.
Keep the PostgreSQL server on 17 for this upgrade; a database-server major
upgrade and a Longhorn-to-local-disk migration are separate changes.

## Validation and limits

Provider 3.2.0.5 passed **174 tests**, with one optional live-schema/log probe
skipped, using the built assembly in a Linux SDK pod connected directly to a
disposable PostgreSQL 17 instance. New tests cover protected/played exclusions,
preferred versions, NULL group keys, count and paging, unrelated SQL, lexical
boundaries, correlated and nested queries, and synchronous/asynchronous cache
buffering of the transformed reads. The release build has no warnings/errors.
On production data, the representative filtered read took 0.225 s and its count
0.148 s with unchanged planner settings. The first-page ID fingerprint matches
the original query executed with a diagnostic session-only planner override.
See `reports/jellyfin-ipc-2026-10-29/` in the parent workspace for activation state,
the IPC explanation, storage findings and validation limits.

The following results describe the earlier 3.2.0.4 deployment:

Provider 3.2.0.4 passed **168 tests**, with one optional live-schema/log probe
skipped, against a disposable PostgreSQL 17 pod. The test host was Windows, with
temporary files redirected into the workspace. The two filesystem tests denied
by the default Windows sandbox temp path passed after this environment change.
New checks cover the generated rating SQL, unchanged ordinary concatenations,
direct/ancestor/linked membership, NULL ratings, counts and paging in synchronous
and asynchronous execution.

Production loaded 3.2.0.4 and returned `Healthy`. Fresh Movies On Demand API
requests took 0.161 s for `18+`, 0.186 s for `18+` plus Netflix, 0.064 s for `18+`
plus Prime Video and 0.271 s for `PG-13` plus Netflix. The latter had timed out
after 15 seconds with the index alone. The original `18+` item IDs and existing
configuration hashes were unchanged. See the parent workspace's
`reports/jellyfin-parental-filter-2026-10-29/` for evidence and rollback details.

The following measurements describe the earlier 3.2.0.3 deployment:

Provider 3.2.0.3 passed **164 tests**, with one optional live-schema/log probe
skipped, against a separate PostgreSQL 17 instance in Linux. Regression checks
cover Jellyfin's explicit single-query hints, provider interceptor registration,
collection membership, images/providers, paging with equal names, unchanged
user-query hints, and synchronous reads that update on the same connection.
The query-shape regression was first reproduced against 3.2.0.2: configuring a
split-query default alone did not override Jellyfin's explicit single-query hint.

Production installed 3.2.0.3 on 2026-10-29 and returned `Healthy`. Authenticated
API checks returned Collections pages in 1.07–1.84 seconds, Movies in 0.20 seconds,
Movies On Demand (19,537 items) in 0.81 seconds, title searches in 0.51–1.28 seconds,
search hints in 1.45 seconds, Latest Movies in 0.21 seconds and Next Up in
0.43 seconds. The 1,101 collections, 58,030 links and three users were preserved;
page counts, disjoint pages and repeated results passed. See the parent
workspace's `reports/jellyfin-collections-spinner-2026-10-29/` for evidence.
At that stage, opening the 4,132-item Netflix collection's contents still took
12–14 seconds. A later native item-cache adjustment improved repeated loads;
see `reports/jellyfin-collection-contents-2026-10-29/` in the parent workspace.

The corrected 3.2.0.1 PostgreSQL 17 test run passed **156 tests**, with **one
optional live-schema/log probe skipped**, out of 157. It includes a populated 12.1 schema
upgrade, repeat migration, metadata/user/password-hash/playback-state
preservation, the filtered indexes, and statistics refresh.

The actual LinuxServer 12.4 image, the packaged ZIP and PostgreSQL 17 client tools
were also exercised in a disposable k3s namespace with a separate emptyDir-backed
database. Both first startup and upgrade of a small synthetic 12.1 fixture were
tested. The fixture used the production migration identifiers, synthetic data,
and conflicting playback records. The real 12.4 server applied the provider
migration and pending code migrations, selected the most recent playback state,
loaded provider 3.2.0.0, and returned `Healthy`. The subsequent 3.2.0.1 build adds
the parameterized rating-update guard and corresponding unit/integration tests.

The production cutover on 2026-10-28 subsequently loaded provider 3.2.0.1 and
completed Jellyfin 12.4 startup. All 635,436 library rows, three users and 15,771
playback rows were retained; user settings and the complete playback-table hash
matched the frozen baseline. The public endpoint was healthy, all 16 plugins
reported Active, and an authenticated original-file range read succeeded.
The large unrated-item cleanup was performed offline in small batches before
the real core rating migration completed. See the parent workspace's
`reports/jellyfin-cutover-12.4-20261028-183439/cutover-result.md`.

This is not a full production restore rehearsal, a playback/transcoding session
test, or exhaustive functional validation of the other installed plugins.

Sources: [Jellyfin 12.4 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.4),
[12.4 SQLite migration](https://github.com/jellyfin/jellyfin/blob/v12.4/src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite/Migrations/20261002210711_MakeOwnerIdIndexesPartial.cs),
and the corresponding tagged source/model used for the generated PostgreSQL migration.
