# Parental Guide for Jellyfin 12.x

Native C# conversion of `../emby_rating_sync.py`. Targets .NET 10 and the Jellyfin 12.0.0 plugin API. No Python, Emby credentials, cron job, or Jellyfin API key is required.

## Install

1. Follow the [repository installation steps](../README.md#install-and-update-through-jellyfin) to install **Parental Guide** from Jellyfin's Catalog. Automatic updates are enabled in catalog packages. For a manual/offline build, use the .NET 10 SDK with `./build.ps1`; it produces `artifacts/ParentalGuide_1.0.0.0_jellyfin12.zip` and a SHA-256 checksum.
2. For a catalog install, restart Jellyfin when requested. For a manual install, stop Jellyfin and extract the DLL and `meta.json` directly into `Parental Guide_1.0.0.0` inside **your Jellyfin server's actual plugins directory**, then start it again. Container distributions use different config/data paths.
3. Start Jellyfin. In **Dashboard > Plugins > My Plugins > Parental Guide**, enter your OMDb API key and save. Dry run is enabled by default. For a small preview, set **Maximum items per run** to e.g. `10`.
4. Open **Dashboard > Scheduled Tasks > Parental Guide Metadata Sync**, start the task, and inspect the server log for `Would update` entries. Progress and cancellation use the normal Jellyfin controls.
5. Once the preview is satisfactory, disable **Dry run** and save. The task runs daily at **3:00 AM in the Jellyfin server's timezone** by default. Edit/remove/add its triggers on the Scheduled Tasks page. For 3 AM Eastern in a container, its server timezone must be configured accordingly.

For SDK/build environments that do not use PowerShell:

```sh
dotnet test ParentalGuide.slnx -c Release
dotnet build Jellyfin.Plugin.ParentalGuide -c Release
```

Install only the plugin DLL from `Jellyfin.Plugin.ParentalGuide/bin/Release/net10.0/` and `meta.json`. Do not copy Jellyfin or Microsoft dependency DLLs from test outputs into the server. The Python script remains available and is not modified; its Emby SQLite state is not imported.

## Behavior and settings

| Setting | Default | Effect |
| --- | --- | --- |
| Dry run | On | Fetch and log proposed changes. Never mutate library metadata or completion records. External API quota is still used. |
| Maximum items | 0 | 0 means unlimited. Caps attempted pending movie/series titles, including failures, forced scans, and dry runs. One title may require several provider requests. |
| Force full scan on next run | Off | Ignore saved completion for that run, then automatically clear the checkbox. Still obeys the item limit. |
| Title/year fallback | On | Match by exact normalized title and supplied year/type if the IMDb provider ID is missing or not found. Reject obvious fuzzy matches. |
| Language tags | On | Add one `Lang:<language>` tag per OMDb language. |
| Parents Guide tags | On | Add every available category's severity as `Parents Guide: <category> - <severity>`, regardless of votes or certification availability. |
| Only upgrade ratings | On | Replace a known existing certification only when the candidate is stricter on the supported US scale. Keep unknown/incomparable ratings and equal levels. Unrated items can receive a certification. |
| Infer from Parents Guide | Off | When OMDb has no certification, optionally infer a conservative US rating from severity votes. |
| Inference minimum votes | 3 | Applies to inference only; never suppresses category tags. |
| Request timeout | 30 seconds | Per request attempt; cancellation also interrupts active requests. |
| Maximum retries | 4 | Additional attempts per provider request, shared by HTTP, transport, and provider-payload failures. |
| Retry delays | 1 to 60 seconds | Exponential backoff with up to 25% jitter. Numeric/date `Retry-After` values are minimum waits and may exceed the calculated cap. |
| OMDb / IMDb request delay | 0 / 0.25 seconds | Optional delay before every request attempt. |

Movies and series are processed; episodes, virtual items, and non-library sources are excluded. OMDb's `Rated` certification is copied to `OfficialRating`. Existing tags are merged case-insensitively, never replaced with just the new tags. The task re-reads metadata after network calls to preserve current ratings and tags. Missing external ratings never erase an existing rating. `CustomRating` is not edited; if set, Jellyfin may use that override for parental access decisions.

All five recognized IMDb categories are considered: **Sex & Nudity**, **Violence & Gore**, **Profanity**, **Alcohol, Drugs & Smoking**, and **Frightening & Intense Scenes**. Only categories actually supplied with a known severity (`None`, `Mild`, `Moderate`, `Severe`) produce tags. Missing categories are not fabricated. Existing Parents Guide and source tags are retained, including old severities if the guide changes later.

Inference follows the original script: any Severe category meeting the vote threshold gives **R** for a movie or **TV-MA** for a series. Lower ratings require sufficient votes in all five categories; maximum None/Mild/Moderate gives G/PG/PG-13 or TV-G/TV-PG/TV-14. An unqualified/missing category can hide severe content, so it prevents a lower inference. An inferred rating adds `Rating Source: IMDb Parents Guide` when guide tagging is enabled. These are heuristics from community severity votes, not official certifications.

The comparison scale is G/TV-Y/TV-G < PG/TV-Y7/TV-Y7-FV/TV-PG < PG-13/TV-14 < R/TV-MA < NC-17. US/USA prefixes and underscores are normalized for comparison. It is deliberately a small US scale rather than a worldwide age-certification mapping. With stricter-only disabled, valid OMDb certifications can replace existing ratings. Inference still preserves an existing certification unless it is empty/unrated or matches a rating previously inferred by this plugin.

## Incremental completion and full scans

Each successful live item is immediately checkpointed in `completed-v1.jsonl` inside Jellyfin's data directory for this plugin (`Plugin.DataFolderPath`). Later runs skip completed identities, so the first live pass can span several limited runs or interruptions. New items and changed title/year/type/IMDb identities are pending automatically. Tag/rating edits do not themselves invalidate completion. Successful no-match/no-rating outcomes are completed too, matching the script. Use a forced scan to retry those when provider data becomes available or to apply changed enrichment settings.

Provider failures and metadata-save failures remain pending. Successfully written items survive cancellation, restart, and a fatal provider failure later in the run. The task continues past ordinary item errors, then reports failure on the Scheduled Tasks page; fatal failures stop immediately. Corrupt or incomplete state fails closed with a log message rather than triggering an unexpected rescan. Back up the state file with Jellyfin's data. If a checkpoint write fails after a metadata save, that item can be revisited safely because updates are additive/idempotent.

**Force full scan is a one-run override**, not a queue for an unlimited sweep. Its checkbox is consumed when a valid run starts, including a dry run. A limit of 10 revisits at most 10 titles; it does not arrange another forced batch. Remove the limit for a complete rescan. Cancellation or fatal failures do not re-enable the checkbox. Attempted forced items that fail in live mode remain pending for the next normal run. Dry runs never complete or invalidate items, so switching from preview to live still processes the pending titles.

## Provider failures and privacy

HTTP 408/425/429, server failures, transport errors, timeouts, invalid JSON, and recognized transient OMDb/IMDb payload errors are retried. HTTP 401/403 and provider authentication errors stop immediately. OMDb API-key/quota/daily-limit errors stop immediately. Persistent HTTP or payload throttling stops the task after the retry budget, preventing requests for every remaining title. Delays, retries, and HTTP calls are cancellable.

The IMDb GraphQL endpoint is the same unofficial endpoint used by the script. Schema/access changes can require a plugin update. Unexpected guide structures are item errors rather than silently completed empty metadata; explicit null/empty guides are valid no-data results. OMDb and IMDb must be reachable from the Jellyfin server.

The OMDb key is saved in Jellyfin's plugin configuration XML; the password field hides it visually and does not encrypt the file. Provider errors and retry messages do not echo credentials, request URLs, or raw bodies. A dedicated HTTP client avoids factory request logging of OMDb's query-string key. Item names, ratings, and tags are logged for previews and auditing.

## Validation and API references

All 69 regression tests passed against Jellyfin 12.0.0 and 12.2.0 during development. The suite exercises dry-run safety, limits, incremental restarts, forced failure retries, cancellation, tag merging, conservative inference, strictness comparisons, title fallback, HTTP/payload authentication and quota failures, throttling, exponential jitter, and `Retry-After` handling. Tests use fake providers and libraries; they spend no API quota and do not modify a running server. Installation and real-provider/access-control checks on a running Jellyfin server remain unverified.

- [Jellyfin 12 scheduled-task interface](https://github.com/jellyfin/jellyfin/blob/v12.0/MediaBrowser.Model/Tasks/IScheduledTask.cs)
- [Jellyfin 12 library interface](https://github.com/jellyfin/jellyfin/blob/v12.0/MediaBrowser.Controller/Library/ILibraryManager.cs)
- [Jellyfin Scheduled Tasks documentation](https://jellyfin.org/docs/general/server/tasks/)

To compile/test against another published 12.x minor, pass `-p:JellyfinVersion=12.2.0` to `dotnet test`. The distributable is built against the baseline 12.0.0 API; .NET 10 is required. Use `build.ps1` with the default package version to produce the release ZIP.
