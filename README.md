# Jellyfin plugins

Source, automated builds, and a Jellyfin installation/update catalog for Preston's plugins.

| Plugin | Current version | Minimum Jellyfin | Purpose |
| --- | --- | --- | --- |
| [Library Bridge](LibraryBridge/README.md) | 0.2.7.0 | 12.4.0 | One search, Next Up, and Continue Watching entry across local/on-demand libraries, with selectable sources and shared progress |
| [Parental Guide](ParentalGuide/README.md) | 1.0.0.0 | 12.0.0 | Native scheduled parental-certification and Parents Guide metadata synchronization |
| [PostgreSQL Database Provider](PostgresProvider/FORK.md) | 3.2.0.7 | 12.4.0 | Modified BORNIOS provider with the existing Jellyfin 12.4 compatibility and query-performance fixes |

## Install and update through Jellyfin

After the first GitHub release is published, add this repository:

```text
https://github.com/prestonr83/jellyfin-plugins/releases/latest/download/manifest.json
```

1. Open **Dashboard → Plugins → Repositories → Add** in Jellyfin.
2. Name it **Preston's Jellyfin Plugins** and paste the URL above.
3. Open **Catalog**, select the desired plugin, and install.
4. Restart Jellyfin when requested, then open the plugin's configuration page.
5. Leave automatic updates enabled. Jellyfin's **Update Plugins** scheduled task discovers compatible newer versions from this same URL. Restart after an update when requested.

Library Bridge starts disabled with a preview. Parental Guide starts in dry-run mode. Installing/updating the DLL does not turn these operational controls on.

The manifest and package downloads must be publicly readable; publish this as a **public GitHub repository**. The URL becomes live only after the first successful release workflow. A private Git repository or Actions build artifact alone cannot serve this unauthenticated catalog.

Older manually installed Library Bridge or PostgreSQL-provider packages may have automatic updates disabled in their `meta.json`. Enable updates in Jellyfin's plugin controls, or install the newer catalog version once through the Catalog. No DLL copying is needed. See [the PostgreSQL fork notes](PostgresProvider/FORK.md) for its preserved identity and upstream catalog interaction.

## Push this prepared repository

This directory is a standalone Git repository. Its expected origin is:

```text
https://github.com/prestonr83/jellyfin-plugins.git
```

The **public** GitHub repository has been created at this origin. The local repository already contains its README, license, and ignore rules. To publish the prepared source and first catalog release:

```powershell
Set-Location C:\Users\Gaming\Documents\ServerStuff\jellyfin
git push -u origin main
git tag v2026.10.30.1
git push origin v2026.10.30.1
```

If authentication is needed, run `gh auth login` in your own terminal or use your normal Git credential manager. No personal access token belongs in this repository or in Jellyfin.

The tag starts **Publish Jellyfin catalog** in GitHub Actions. It builds and tests all three plugins, including PostgreSQL integration tests against a disposable PostgreSQL 17 service, creates ZIPs and the catalog, uploads everything to a draft release, and publishes that release only after all uploads are complete. The repository URL above then works in Jellyfin.

The catalog tag identifies a bundle release; each plugin keeps its own version. The example date is only a tag name, not a release schedule.

## Development and validation

Requirements: .NET 10 SDK and Python 3.12 or newer. No Python packages or extra GitHub secrets are required.

```powershell
python -m unittest discover -s scripts/tests -v
python scripts/build_repository.py --tag local-validation
python scripts/release_repository.py publish --tag local-validation --dry-run
```

Builds explicitly use each plugin's declared Jellyfin API baseline. Library Bridge's native query tests compile the actual pinned Jellyfin 12.4 source into its test host and use a disposable SQLite database. That source is downloaded once into `.work/` and is not committed or shipped. Parental Guide is built/tested against 12.0.0 to preserve its declared ABI floor.

Local options:

```powershell
python scripts/build_repository.py --dotnet C:\path\to\dotnet.exe --jellyfin-source C:\src\jellyfin-12.4 --packages C:\path\to\nuget-cache
```

`--no-restore` reuses an already restored, matching dependency graph. `--jellyfin-source` avoids the source download when an unpacked 12.4 tree is already available.

Generated files go to ignored `dist/`:

- Each new plugin ZIP contains that plugin's DLL and `meta.json`, with automatic updates enabled. The PostgreSQL provider additionally includes its two explicitly allowed Npgsql dependency DLLs; server/framework DLLs are excluded.
- `manifest.json` contains compatible versions, immutable release download URLs, changelogs, and Jellyfin's required **MD5 of the ZIP**.
- `.sha256` sidecars provide an additional SHA-256 checksum.
- `release-summary.json` and `release-notes.md` describe the bundle.

ZIP contents/order/timestamps are deterministic. CI builds on Linux and Windows and runs a separate PostgreSQL 17 integration job; publishing uses Linux and requires those provider integration tests too. Local/Windows runs without `POSTGRES_TEST_CONNECTION` skip the tests that require a live disposable PostgreSQL service. Backup-client tests and the optional live-server schema/log probe remain explicit opt-ins. Action dependencies and the native-test source are pinned to reviewed commit SHAs.

## Release an update

1. Make the code change and increase that plugin's four-part `version` in its `meta.json`. Keep its project/`Directory.Build.props` version and individual build documentation in sync. Change `targetAbi` and `repository.json`'s `jellyfinVersion` together only when raising the Jellyfin API baseline.
2. Update that plugin's `changelog` in `repository.json`.
3. Commit and push `main`; let **Build and test plugins** pass.
4. Create and push a new `v...` tag, or run **Publish Jellyfin catalog** manually with an existing tag.

For example:

```powershell
git add .
git commit -m "Improve Library Bridge resume handling"
git push
git tag v2026.10.31.1
git push origin v2026.10.31.1
```

The release workflow reads the previous published catalog and preserves older plugin/ABI versions. An unchanged plugin keeps its previously published asset URL/checksum. If runtime inputs change without a version bump, publication fails instead of silently replacing that version. Published releases are immutable; retries can resume an incomplete draft, while an already published tag requires a new tag.

Repository automation uses GitHub's built-in `GITHUB_TOKEN` with write access only in the release job. It does not need permission to push generated commits or bypass branch protection. No GitHub Pages deployment is required.

## Local-only content

`Jellyfin-Database-Providers-Postgres/` remains a separate, ignored Git checkout with its existing history and local modifications. Its current modified source was imported into the tracked `PostgresProvider/` directory for this catalog; make future catalog-provider edits there. The import is ordinary source, not a submodule pointing at an unpublished commit. Upstream licensing and attribution are preserved. The legacy `emby_rating_sync.py`, `web-filter-fix/`, downloaded SDKs, caches, server state, and generated artifacts are also excluded from this Git repository.

Build/test success is separate from live-server validation. Catalog installation and real-client playback should be checked after publishing the first release.
