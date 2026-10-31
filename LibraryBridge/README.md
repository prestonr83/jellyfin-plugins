# Library Bridge for Jellyfin 12.4

Library Bridge groups matching titles across paired libraries in search, Next Up, and Continue Watching while retaining their existing library membership, IDs, media folders, files, and STRM URLs. It supplies selectable Local / On Demand sources through Jellyfin's playback API and synchronizes watched status and resume progress.

Version **0.2.6.0** targets **Jellyfin 12.4.0 on .NET 10**, fixes Continue Watching deduplication for lightweight client requests, adds responsive settings controls and fixes recursive library discovery and early-startup initialization, includes Continue Watching deduplication, and supports catalog installation/automatic updates. It is disabled when first installed. It has local automated coverage, including nested catalog discovery, early persistence initialization and Jellyfin's actual search/grouping, Next Up, and resume-query code on a disposable SQLite database. Production installation, your PostgreSQL provider, and playback in your actual clients still require validation.

## Install and preview

1. Follow the [repository installation steps](../README.md#install-and-update-through-jellyfin) to install **Library Bridge** from Jellyfin's Catalog. For a manual/offline build, `build.ps1` also creates `artifacts/LibraryBridge_0.2.6.0_jellyfin12.4.zip` containing the DLL and descriptor; extract those directly into `Library Bridge_0.2.6.0` inside the server's actual plugins directory.
2. Restart Jellyfin when requested and open **Dashboard → Plugins → Library Bridge**.
3. Adjust the library pairs to your exact names or IDs. Defaults are `Shows` ↔ `Shows On Demand` and `Movies` ↔ `Movies On Demand`.
4. Choose **Preview matches**. Preview reads all physical roots of each configured library without changing any item or watch history. The report includes group IDs, member item IDs, counts, and conflicting identities that were skipped. Empty libraries and pairs with no matches produce warnings with title counts and matching requirements. No matching-name fallback is used. `Applied: false` is expected for a preview; **Enable and apply** activates grouping.
5. Review a movie and a show that exist in both libraries. **Enable and apply** changes their presentation keys and, if selected, reconciles existing watch history. Back up Jellyfin's database and plugin data before first activation.
6. Check global search, Next Up, and Continue Watching; browse each original library; open a title/episode and exercise both playback versions. Version selector placement depends on the client. Check one account that cannot access the on-demand library as well.

No deployment or production restart is performed by the build script.

## Matching and behavior

- Titles require at least one valid shared TMDb, TVDb, or IMDb identity and must occur on both sides of a configured pair. Conflicting IDs reject the entire connected match group. Movies and series cannot match each other. Unknown/zero IDs are ignored.
- Episodes additionally require the same season, episode, and episode range under a matched series. Different DVD/absolute/airdate orders are kept separate; default and explicit airdate order are equivalent. Specials use season zero.
- Seasons and episodes share a presentation key for their matched series. Episodes available on only one side remain available in the combined show detail. Files spanning multiple episodes do not match a single episode.
- Items already belonging to native version groups are skipped for new version grouping. Their containing show's series identity can still be grouped. Configure this plugin before bulk-running a separate merge plugin; avoid having two tools manage the same items.
- Global search and Next Up use Jellyfin's native grouping **before pagination**, including correct result counts. A library-scoped query keeps that library's own copy. There is no JavaScript search-card hiding.
- Continue Watching keeps Jellyfin's native permission, library, resume, and ordering filters, then collapses verified bridge groups **before pagination**. Page sizes and total counts refer to logical items. Each surviving row retains that actual version's resume position, and both playback choices remain available. Distinct episodes remain distinct resumable items. Active-session exclusions apply to all copies of the matched item.
- Version choices are native file/STRM media sources with the configured labels. Jellyfin handles probing, direct play, transcoding, subtitles, and stream URLs. Selecting a STRM source directs playback negotiation to that source's actual item, so its streams can be probed normally.
- User access is checked for the displayed item and every alternate source. No alternates are returned for an unauthenticated or userless request. API-key requests need an explicit `userId` query parameter to expose user-specific alternate sources.
- Watched state, resume position, play count, and last-played time are copied only within that user's accessible copies. Audio/subtitle selections, favorites, and ratings stay specific to each item. Positions scale by duration when both durations are known; with an unknown STRM duration the reported position is used.
- Existing-history reconciliation keeps any completed watch and otherwise uses the most recently played version. Future explicit mark-unwatched events propagate as unwatched. Initial reconciliation can be disabled separately from future synchronization.
- Index refresh happens after startup, on catalog changes (debounced), and periodically. The default interval is 15 minutes; catalog changes are checked every 30 seconds. A native scheduled task allows a manual refresh or a preview when disabled. Catalog discovery reads IDs from each library's physical roots, then loads metadata in batches of at most 128 items without image or user-history joins.

Client-specific cached lists need reloading after activation. External search providers that truncate their own candidates can omit matches before Jellyfin receives them. Continue Watching processes the user's filtered in-progress candidates rather than the entire catalog; a profile with an unusually large resume list can therefore cost more to load than Jellyfin's original limited page.

## Recovery and uninstall

Use **Disable and restore original grouping** before uninstalling. The plugin restores only the presentation/series keys it recorded and leaves other metadata alone. Disabling also stops source injection and future watch synchronization. **Watch history already synchronized is retained; restoration does not undo it.**

The write-ahead journal is `recovery-v1.json` in Jellyfin's plugin data directory for Library Bridge. Keep it with your database backup. Original values are flushed before native persistence saves the new keys. Cancellation, a failed write, or an interrupted restore leaves the remaining entries available for retry. New seasons discovered while their series is bridged recover using the saved original series identity.

A corrupt journal prevents new grouping writes. Missing or externally changed items are never blindly overwritten: removed items are retired from recovery, while changed keys/identities are reported and retained for inspection. Do not delete the journal while grouping is active. If you uninstall without restoring, the stored grouping can remain until restored from the journal or regenerated by a full metadata refresh.

## API

All four endpoints require Jellyfin's `RequiresElevation` policy:

| Method | Path | Action |
| --- | --- | --- |
| GET | `/LibraryBridge/Status` | Enabled state, latest report, last refresh error |
| POST | `/LibraryBridge/Preview` | Read-only match report |
| POST | `/LibraryBridge/Apply` | Enable, rebuild, apply, and optionally reconcile history |
| POST | `/LibraryBridge/Restore` | Disable and restore recorded grouping keys |

Normal Jellyfin authentication applies. No API key belongs in the plugin configuration or source code.

## Build and test

Use a .NET 10 SDK:

```powershell
.\build.ps1 -Dotnet 'C:\path\to\dotnet.exe'
```

The script restores explicit dependencies, builds with warnings as errors, runs the tests, and packages only this plugin's DLL and descriptor. Jellyfin and ASP.NET runtime assemblies are supplied by the server.

To additionally compile and exercise Jellyfin's **actual 12.4** item query and Next Up implementations, supply an unpacked upstream 12.4 source tree:

```powershell
.\build.ps1 -Dotnet 'C:\path\to\dotnet.exe' -JellyfinSourceRoot 'C:\src\jellyfin-12.4'
```

These optional contract tests use a fresh in-memory SQLite database and never connect to the running server. They verify search and resume deduplication, result totals, pagination, per-library visibility, active-session exclusions, Next Up progression, and restoring separate results. Source files are linked at build time; they are not copied into or shipped with the plugin.

When `POSTGRES_TEST_CONNECTION` points to a disposable PostgreSQL server with permission to create databases, native tests also create and remove isolated `librarybridge_test_*` databases. They verify bounded catalog discovery with large Unicode fields, PostgreSQL split queries, and the provider's buffered cache enabled and disabled. The release workflow runs these tests against its temporary PostgreSQL service; ordinary builds skip them when the variable is absent.

## Implementation

The plugin decorates `IItemPersistenceService` at registration time to maintain presentation keys at the normal save boundary, and `IItemRepository` to group filtered resume candidates before paging. The resume wrapper delegates ordinary queries unchanged and uses the underlying repository for all access filtering and data retrieval. It does not patch Jellyfin binaries, execute direct SQL, install browser scripts, move media, change library definitions, or set native alternate-version ownership links. Playback uses an `IMediaSourceProvider` plus MVC filters for standard DTO source counts/labels and selection/progress routing. A background worker maintains the immutable matching index and watch synchronization.

The host integration targets 12.4 and should be rechecked before installing on another server version. Live smoke testing remains separate from passing local tests.
