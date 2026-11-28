# Backup and restore
Issue #50. How to back up and restore a ZIRRUS Compose deployment, and how to recover from a failed migration. Targets are proposals: RPO 24 h and RTO 4 h on the reference dataset ([deployment-delivery.md](deployment-delivery.md#backuprecovery), Q4).

## What a backup contains
`deploy/backup/backup.sh <dir>` writes one consistent checkpoint:

| File | Content |
|---|---|
| `db.dump` | PostgreSQL custom-format dump: assets, metadata, categories, memberships, job queue, sessions, audit trail and digests |
| `blobs.tar` | the `blobs` volume: originals and renditions, without temporary uploads |
| `manifest.tsv` | every blob the database references: kind, asset ID, key, SHA-256, size; sorted by key, so the same data gives the same file |
| `backup.json` | start time, last applied migration, asset, audit event and manifest counts |
| `SHA256SUMS` | checksums of the four files |

Not included, and backed up separately:
- **Secrets** (`deploy/secrets/`): store them in the organization's secret store, never next to the backup.
- **Exported audit digests** (`audit_digests` volume): copy them off-host on their own schedule ([audit-operations.md](audit-operations.md#digest-export-and-backup)).
- **Identity provider data** (Keycloak or another OIDC provider): back it up with that system.

## Running a backup
1. Run `deploy/backup/backup.sh /srv/backups/zirrus-$(date -u +%Y%m%dT%H%MZ)` on the host, from the repository checkout.
2. The script stops `api` and `worker`, so no asset, rendition, purge or audit write happens during the checkpoint. The web front end keeps serving the app but API calls fail until the backup ends; run it in a maintenance window. It restarts both services when it finishes or fails.
3. Before restarting, it checks every manifest entry's SHA-256 against the volume and fails on any mismatch: a backup of a corrupt store is reported, not silently kept.
4. Encrypt the directory and copy it off-host (below). Schedule this daily to meet the 24 h RPO.

## Encryption and off-host copies
The script writes plaintext. Encrypt before the data leaves the host, for example with [age](https://age-encryption.org):

```sh
tar -C /srv/backups -cf - zirrus-20261128T0200Z | age -r "$BACKUP_RECIPIENT" > zirrus-20261128T0200Z.tar.age
```

- **Keys:** the private key that decrypts backups lives in the organization's vault, not on the host and never with the backups. Record who can recover it; test recovery with each restore drill.
- **Storage:** an off-host location the ZIRRUS host cannot delete from (object storage with versioning or retention lock, or a separate backup server).
- **Retention:** keep backups no longer than the approved audit and asset retention plus one backup cycle; older backups still contain purged assets and audit events (#49, [audit-operations.md](audit-operations.md#retention)).

## Restoring
Restore into a fresh deployment: a new host, or the same host after `docker compose down -v` once you are sure the old data is not needed.

1. Check out the release that made the backup (`lastMigration` in `backup.json`) or a newer one, and provide `deploy/secrets/` (the original secrets, or new ones from `init-secrets.sh`; new secrets end existing sessions).
2. Decrypt the backup into a directory.
3. Run `deploy/backup/restore.sh <dir>`. It:
   - checks `SHA256SUMS` before changing anything, and refuses to run if a `pgdata` or `blobs` volume already exists;
   - restores the database (creating the `zirrus_app` role first, since roles are not part of a database dump) and the asset store;
   - verifies every original's and rendition's SHA-256, the asset and audit event counts, and that the restored database produces the same manifest;
   - starts the stack: migrations run and `db-roles` sets the application login, as on any deployment.
4. After the restore: pending jobs resume from the restored queue; reconciliation reports anything still inconsistent. Missing renditions can be rebuilt by retrying the asset.
5. Record the duration the script prints and the data-loss window (time of the incident minus `startedAt`) as Q4 evidence.

## Failed migration recovery
Migrations run in the one-off `migrate` container before the API and worker start.

1. **Before every upgrade:** run a backup and keep it until the new release has run for a day.
2. **Migration fails:** `migrate` exits non-zero and the API and worker do not start, so the old schema is not used by new code. Check the migration log.
3. **Schema still compatible:** if the failed migration left no changes (EF Core normally applies each migration in its own transaction; confirm with `__EFMigrationsHistory`), redeploy the previous image tag.
4. **Otherwise:** fix forward with a corrected release, or restore the pre-upgrade backup with the previous release. A restore loses every change since the backup, so it needs explicit approval of that data loss.

## Evidence and limits
- **CI:** the compose job backs up the stack after the end-to-end journeys, destroys every volume, restores, and checks checksums, counts, the application role's audit restrictions and that the stack serves again. It proves the tooling on a small dataset, not the RTO.
- **Q9 in CI:** the same job then takes a pre-upgrade backup, simulates a migration that failed half-way (a renamed column and an unknown history row, applied as the owner), recovers by restoring that backup, and runs `tools/ci/recovery-check.py`: the administrator downloads every active original and each SHA-256 matches the asset record and the manifest; a user without a grant sees no catalogs and gets 404. A real failing migration is not injected, because the migration bundle is built into the release image.
- **Release gate:** a timed restore drill on a clean, isolated reference host with the reference dataset, recording duration, data-loss window, 100 % checksum verification and authorization checks (#50 acceptance criteria).
- **Downtime:** the API is unavailable while the backup runs; online backups (filesystem snapshots or continuous WAL archiving) are not implemented.
- **S3 storage:** the scripts back up the filesystem store. With the S3 adapter, back up the bucket with the provider's versioning or replication and keep the manifest to verify it.
