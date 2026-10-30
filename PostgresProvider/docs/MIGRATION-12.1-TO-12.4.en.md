# Upgrade plan: this k3s installation, Jellyfin 12.1 to 12.4

Prepared against the live installation on **2026-10-28**. This is a cutover
runbook; production has not been upgraded by this task.

## Observed source and intended target

| Component | Source | Target |
| --- | --- | --- |
| Namespace / deployment | `jellyfin` / `jellyfin` | Same |
| Server | `12.1.0`, LinuxServer `12.1ubu2604-ls51` | `12.4.0`, based on `12.4ubu2604-ls57` |
| Provider | `3.0.4.0`, automatic updates disabled | This branch's `3.2.0.1`, ABI `12.4.0.0` |
| PostgreSQL | `jellyfin-postgres-0`, PostgreSQL 17.11, database/login `jellyfin` | Same instance, version, database and login |
| DB storage / placement | `data-jellyfin-postgres-0`, Longhorn, node `k3s-server-01` | Same |
| Config storage | `jellyfin-config`, application on `k3s-server-02` | Same |
| Media / metadata | `jellyfin-media-smb`, `/media`, `/config/data/metadata` from `.jellyfin-metadata` | Same paths and mounts |
| Existing database | 635,370 `BaseItems`, 3 users at inventory | Preserve; compare fresh cutover counts |

The old image reference is
`ghcr.io/linuxserver/jellyfin:12.1ubu2604-ls51@sha256:b0b6d034aa52ed6e1a76daaa3d7ed29039d2b802a50fa8ffd1d9fc8d3fd836c7`.
The new base is pinned in `Dockerfile.jellyfin124`. Build and push the derived
image with PostgreSQL 17 client tools to a registry you control, then record
**that derived image's digest** for deployment. The bare new base image failed
the smoke test because `pg_dump` was missing; adding client 17 resolved it.

Keep the existing 16Gi PostgreSQL memory limit, 1Gi shared buffers, role-level
128MB `work_mem`, query DOP 4, and maintenance-worker settings. This upgrade
does not move the database to local disk or change the backup architecture.
It also does not establish that the reported slow Latest Media queries are fixed.

## 1. Prepare and rehearse

1. Build the ZIP with `Tools/Build-Jellyfin124.ps1`, or use the supplied archive
   and verify its SHA-256 sidecar. See `docs/JELLYFIN-12.4.md` for the tested scope.
2. Build the application image on a trusted builder, from this repository:

   ```powershell
   docker build -f Dockerfile.jellyfin124 -t jellyfin-postgres:12.4-3.2.0 .
   ```

   Tag/push it to your registry and record its immutable digest. Verify
   `pg_dump --version` and `pg_restore --version` report major **17** inside it.
   The final Docker build/push has not been performed by this task. The same
   installer was tested inside the exact pinned LinuxServer base image.
3. Recheck both Longhorn volumes, available space on the config PVC and backup
   destination, and wait for replica rebuilding to finish. The last PostgreSQL
   volume sample was healthy with a replica still rebuilding at 99%.
4. Inventory all plugins. The observed additional plugins were Auto Collections,
   Fanart, File Transformation, Home Screen Sections, JustWatch, Open Subtitles,
   Plugin Pages, TheTVDB and Trakt. Validate their 12.4 versions and dependencies
   in staging. Begin staging with this provider plus the built-in plugins, then
   add third-party plugins back in compatible groups.
5. Restore a **paired copy** of production PostgreSQL and `/config` into an
   isolated staging instance. Point both `database.xml` and the provider XML at
   the staging database. Block its access to the production database; omit media
   mounts or make separate staging mounts read-only. Disable scheduled scans,
   metadata writes and external plugin actions while testing the clone.
6. Apply this provider and the target image to staging. Check migrations,
   restart, sign-in, watched/favorite/resume state, adult and child profiles,
   library browsing/search/latest views, playlists, direct play and a transcode.
   Record before/after counts and sample playback records. Use query-statistic
   deltas for performance comparisons; the current slow-query averages include
   history since October 17.

The completed tests used an isolated PostgreSQL server and synthetic data, not
a restore of the full production dataset/config. That full-size rehearsal,
other-plugin compatibility and real playback remain production cutover gates.

## 2. Start the maintenance window and create a paired rollback point

Use the `ServerStuff` workspace for the following PowerShell examples. Recheck
the live namespace and image first. Capture the deployment and CronJob state
before changing them, including the original backup `suspend` value.

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRel = "pvc-migrations/jellyfin-upgrade-12.4-$stamp"
New-Item -ItemType Directory -Path $backupRel | Out-Null
Set-Content -LiteralPath "$backupRel/.gitignore" -Value '*'
kubectl -n jellyfin get deployment jellyfin -o yaml > "$backupRel/deployment-before.yaml"
kubectl -n jellyfin get cronjob jellyfin-postgres-backup -o yaml > "$backupRel/backup-cronjob-before.yaml"
kubectl -n jellyfin patch cronjob jellyfin-postgres-backup --type merge -p '{"spec":{"suspend":true}}'
kubectl -n jellyfin scale deployment jellyfin --replicas=0
kubectl -n jellyfin wait --for=delete pod -l app=jellyfin --timeout=120s
```

Confirm no backup job, scan, restore or index operation is active. Stop other
Jellyfin database writers for the backup interval. Leave PostgreSQL running.

Take a fresh custom-format PostgreSQL dump **after stopping writers**. Existing
nightly backups are useful additional copies, not a substitute for this paired
cutover backup. Preserve ownership/ACL metadata; the roles stay in the same
PostgreSQL instance. Keep credentials and these archives outside Git.

```powershell
kubectl -n jellyfin exec jellyfin-postgres-0 -- pg_dump -U postgres -d jellyfin -Fc -f /tmp/jellyfin-pre124.dump
kubectl -n jellyfin exec jellyfin-postgres-0 -- pg_restore --list /tmp/jellyfin-pre124.dump
kubectl -n jellyfin exec jellyfin-postgres-0 -- sha256sum /tmp/jellyfin-pre124.dump
kubectl -n jellyfin cp jellyfin-postgres-0:/tmp/jellyfin-pre124.dump "./$backupRel/postgres.dump"
Get-FileHash -Algorithm SHA256 -LiteralPath "$backupRel/postgres.dump"
```

Check each exit code and compare the hashes. `pg_restore --list` validates the
archive directory; the earlier staging restore is what validates recoverability.
Use native `kubectl cp` for the binary archive, not a PowerShell text pipeline.

The helper example is under
`jellyfin/Jellyfin-Database-Providers-Postgres/examples/k3s-upgrade-files.yaml`.
Recheck the current config-volume node and adjust its node selector if needed.
Validate it with client/server dry runs, create it only while Jellyfin is stopped,
and wait for Ready. It mounts only the existing config PVC; it has no media mount.

```powershell
$helper = 'jellyfin/Jellyfin-Database-Providers-Postgres/examples/k3s-upgrade-files.yaml'
kubectl apply --dry-run=client -f $helper
kubectl apply --dry-run=server -f $helper
kubectl apply -f $helper
kubectl -n jellyfin wait --for=condition=Ready pod/jellyfin-upgrade-files --timeout=120s
kubectl -n jellyfin exec jellyfin-upgrade-files -- tar -C /config --exclude='./data/metadata' --exclude='./cache' --exclude='./data/transcodes' -czf /tmp/jellyfin-config-pre124.tgz .
kubectl -n jellyfin exec jellyfin-upgrade-files -- sha256sum /tmp/jellyfin-config-pre124.tgz
kubectl -n jellyfin cp jellyfin-upgrade-files:/tmp/jellyfin-config-pre124.tgz "./$backupRel/config.tgz"
Get-FileHash -Algorithm SHA256 -LiteralPath "$backupRel/config.tgz"
```

Verify archive contents and hashes. Preserve `database.xml`, provider
configuration, all plugin DLLs/metadata, system/network/encoding configuration,
users/config state and relevant Kubernetes secrets through the existing secure
secret-backup process. The config tar contains credentials: restrict access.
Take a corresponding NAS snapshot/backup of `.jellyfin-metadata` separately;
the helper does not include that SMB-mounted directory. Do not rename media
folders or change their mappings.

## 3. Install the provider offline and switch the image

Extract the supplied ZIP locally and copy **only its four entries** to the
helper's `/tmp/provider-stage`. Never copy `artifacts/publish/` wholesale.

```powershell
$package = 'jellyfin/Jellyfin-Database-Providers-Postgres/artifacts/Jellyfin.Database.Providers.Postgres-3.2.0.1-jellyfin12.4.zip'
Expand-Archive -LiteralPath $package -DestinationPath "$backupRel/provider-stage"
kubectl -n jellyfin cp "./$backupRel/provider-stage" jellyfin-upgrade-files:/tmp/provider-stage
```

Open a shell in the helper with `kubectl -n jellyfin exec -it jellyfin-upgrade-files -- sh`.
With the backups verified, run the following in that helper shell:

```sh
set -eu
backup="/config/plugin-recovery/pre-12.4-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$backup"
for folder in /config/data/plugins/PostgreSQL\ Database\ Provider*; do
    [ -d "$folder" ] || continue
    mv "$folder" "$backup/"
done
target='/config/data/plugins/PostgreSQL Database Provider_3.2.0.1'
mkdir -p "$target"
cp /tmp/provider-stage/Jellyfin.Database.Providers.Postgres.dll "$target/"
cp /tmp/provider-stage/Npgsql.dll "$target/"
cp /tmp/provider-stage/Npgsql.EntityFrameworkCore.PostgreSQL.dll "$target/"
cp /tmp/provider-stage/meta.json "$target/"
chown -R 99:100 "$target"
ls -l "$target"
```

Keep the provider's existing configuration and `database.xml`; they already
select `PLUGIN_PROVIDER` and the correct PostgreSQL connection. Do not run a
SQLite import/export, reset script, fresh-install procedure or database purge.

Exactly **one** provider directory must remain under `/config/data/plugins`.
Jellyfin's database loader sorts matching directory names and picks the first;
leaving the old `3.0.4` folder there can select it even when a newer plugin is
active. Backups belong outside the plugins tree. The packaged `meta.json` also
restricts regular assembly discovery to the plugin DLL and disables auto-update.

Delete **only the helper pod**, leaving the PVC intact. Set the deployment image
to the derived 12.4 image **by digest**, then scale Jellyfin to one replica.
Use `kubectl set image ... --dry-run=server -o yaml` before the real image update.
Preserve all current environment variables, security context, GPU, volume
mounts, paths, affinity, service and ingress settings. PostgreSQL is not restarted.

```powershell
# Substitute the derived image built in step 1, including its registry digest.
$image = '<your-derived-image>@sha256:<its-digest>'
kubectl -n jellyfin delete pod jellyfin-upgrade-files --wait=true
kubectl -n jellyfin set image deployment/jellyfin "jellyfin=$image" --dry-run=server -o yaml
kubectl -n jellyfin set image deployment/jellyfin "jellyfin=$image"
kubectl -n jellyfin scale deployment jellyfin --replicas=1
kubectl -n jellyfin rollout status deployment/jellyfin --timeout=600s
```

## 4. Validate before reopening service

- Watch startup through `kubectl logs` and rollout status. The provider migration
  replaces the two OwnerId indexes during the maintenance window; it is not a
  concurrent reindex of the whole database.
- The observed 12.1 code history also leaves these 12.4 migrations pending:
  `HarmonizeConflictingUserData`, `FixNullEncoderPreset` and the newer
  `MigrateRatingLevels`. Let Jellyfin run them. Rating recalculation may take
  time on this library; do not restart/cancel it merely because startup is slow.
- Require Ready, `/health` = `Healthy`, `/System/Info/Public` version `12.4.0`,
  provider `3.2.0.1` loaded once, and `Startup complete`. Reject assembly-load,
  pending-model, permission, migration or backup failures.
- Verify migration `20261028220956_Jellyfin124`, both filtered indexes, zero
  invalid indexes, preserved old migration rows, and expected data counts.
  Watched-state conflicts may be harmonized to the most recent state by the
  upstream migration; the synthetic rehearsal verified that behavior.
- Repeat the staging checks for real users and media. In particular verify
  child-profile rating visibility after rating recalculation, authentication,
  resume positions, metadata writes, direct play and hardware transcode.
- Restart once and verify no migration repeats. Re-enable compatible third-party
  plugins deliberately, and keep this custom provider's automatic updates off.
- Restore the backup CronJob's saved suspend value (observed `false`). Its
  schedule is 02:00 America/New_York. Run and inspect a new backup after the
  upgrade; retain the pre-upgrade paired backup beyond normal retention.
- Update `k3s/jellyfin/manifests.yaml`
  with the final derived image digest and the deployment documentation after a
  successful cutover, so a later apply cannot revert the live image.

## 5. Roll back from the paired backup if acceptance fails

Keep users out and stop Jellyfin again. Suspend backup jobs. Preserve failure
logs and the failed 12.4 config/database for diagnosis before replacing anything.

1. Retain the failed PostgreSQL database under a separate name after disconnecting
   its sessions. Recreate `jellyfin` with owner `jellyfin` and restore the verified
   pre-upgrade custom dump as PostgreSQL administrator, allowing its original
   ownership/ACL metadata to be restored. Test the restored login/ownership.
2. Restore `/config` from the **matching** tar into a clean config-volume copy
   or restore point. Do not merely overlay old files on top of the new tree:
   that can leave both provider versions present. Keep the failed config copy.
3. Restore the previous NAS metadata snapshot if the new server changed metadata.
   Preserve existing media paths and mounts throughout.
4. Restore the saved 12.1 image/deployment and provider 3.0.4 files together.
   Confirm exactly one provider folder and `autoUpdate=false` before starting.
5. Start Jellyfin, verify 12.1 health/login/playback/user data, and then restore
   the backup schedule. Keep all pre-upgrade and failed-state backups until the
   incident is resolved.

An image-only rollback is insufficient after server migrations. Do not run the
12.1 server against the upgraded database and config. Do not delete either PVC,
run the old reset script, or overwrite the only rollback backup.
