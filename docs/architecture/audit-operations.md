# Audit operations
Issue #48. How to run the audit trail described in [security.md](security.md#audit-and-retention): key handling, rotation, retention, failure behavior and the trust boundary. Retention periods are proposals until approved (see [Open decisions](#open-decisions)).

## Trust boundary
The audit trail detects changes made outside the application. It is not tamper-proof against an administrator who controls both the database owner account and the digest key: such a person can rewrite events, digests and purge evidence consistently.

- **Detected:** changes by `zirrus_app` (the API and worker), by the owner account without the digest key, and by anyone who edits the database without also re-signing the digests.
- **Limited, not prevented:** collusion of owner account and key. Off-host copies of the exported digests and of the key make a later rewrite visible when compared with those copies.
- **Not covered:** host root access to the PostgreSQL data files, the worker's memory or the secret files.

## Digest key
| Item | Value |
|---|---|
| Secret | `deploy/secrets/audit_digest_key`, mounted as `Audit__DigestKey` into the worker only |
| Format | base64, at least 32 bytes; `init-secrets.sh` creates 32 random bytes |
| Key ID | first 8 hex characters of SHA-256 over the key bytes, stored on every digest (`key_id`) |
| Who needs it | the worker (seals and verifies); nobody else at runtime |

- **Storage:** keep the secret file readable only by the deployment user (`umask 077`). Keep one copy in the organization's secret store or offline vault, separate from the host and its backups, labeled with its key ID and the date it took effect.
- **Never** put the key in images, environment files committed to Git, logs or tickets.
- **Without a valid key** the worker logs `Audit digests disabled` as a warning at every run and seals nothing. Events are still recorded and stay append-only, but they are not signed, and retention cannot remove them (only sealed ranges are purged).

## Key rotation
Rotate when the key may have been exposed, when a person with access to it leaves, and otherwise at least once a year.

1. Generate a new key: `openssl rand -base64 32 | tr -d '\n'`. Record its key ID, for example with `base64 -d <<<"$KEY" | sha256sum | cut -c1-8`.
2. Store the **old** key in the vault under its key ID; it is still needed to verify digests it signed.
3. Replace `deploy/secrets/audit_digest_key` and redeploy the worker.
4. Check the next run: a new digest is sealed with the new key ID, continuing the chain (its `previous_sha256` is the last digest signed with the old key).

Expected behavior and current limits:

- The worker verifies with **one** key. Older digests within the verification window (`Audit:VerifyRecent`, default 48 digests) report `UnknownKey` as a warning until newer digests push them out of the window. This is expected after a rotation; a `Mismatch` is not.
- Old digests remain verifiable only with the old key. There is no offline verifier tool yet, so verifying them means running `AuditDigests.VerifyAsync` with the old key against a restored copy of the database.
- After a suspected key compromise, compare the database digests with the off-host exports from before the suspected exposure: any digest sealed after it may have been forged.

## Digest export and backup
The worker writes each digest to `Audit:ExportDirectory` (`/var/lib/zirrus/audit`, volume `audit_digests`) as `audit-digest-<to_seq>.json`, atomically via a temporary file and rename. A digest committed in the database but not yet exported is exported on the next run.

- Copy the export directory off-host on its own schedule, at least daily, to a location the database owner cannot write to.
- Back it up separately from `pgdata`: comparing the two copies is what reveals a consistent rewrite inside the database.
- After a database restore, the exports newer than the backup point show which digests (and so which event ranges) were lost.

## Retention
Retention is a maintenance task run by an operator with the **owner** account; nothing schedules it yet. `zirrus_app` cannot run it.

```sql
SELECT audit_purge(now() - interval '180 days');  -- returns the number of events removed
```

- **Whole digest ranges only:** a range is removed only when every event in it is older than the cutoff and all its events are still present. A range with any younger event stays until all of it has expired, so events can be kept longer than the retention period by up to one digest interval.
- **Evidence:** before deleting, the function writes the sequence number and ID of every removed event to the append-only `audit_purged_events` ledger. Verification then reports the digest as `Purged`, not `Mismatch`.
- **Unsealed events** are never removed. Events younger than the safety lag, or written while no key was configured, are sealed by the next run that has a valid key and only then become eligible.
- **Backups:** events removed from the database remain in older database backups until those expire. Backup retention must not exceed the approved audit retention by more than the backup cycle.

## Failure behavior
| Situation | Behavior | Log level | Operator action |
|---|---|---|---|
| No or invalid key | Nothing sealed; events still recorded | Warning, every run | Configure the key |
| Database unavailable or run fails | Run aborted, retried at the next interval (default 1 h) | Error | Restore the database; no events are lost |
| Export directory not writable | Digest committed in the database, export retried on later runs | Error | Fix the volume; the missing files are written on the next successful run |
| `UnknownKey` | Digest signed with a different key; not verified | Warning | Expected after rotation; otherwise investigate the key configuration |
| `Mismatch` | Sealed events changed, inserted, removed without evidence, reordered or truncated; or chain or signature broken | **Critical** | Treat as a security incident (below) |
| Audit entry for a change cannot be written | The request's transaction rolls back, so the change is not applied either | Error | Investigate the database; no unaudited change is committed |
| Access-denied (403/404) event cannot be written | The response is already sent; the event is lost | Error (`Failed to persist an access-denied audit event`) | Investigate the database; correlate with API access logs |

Alert on every `Critical` log entry and on more than one consecutive failed digest run.

## Responding to a mismatch
1. Preserve evidence: snapshot the database and copy the export directory before anything else changes.
2. Identify the range from the log entry (`seq From–To`) and compare those events and the digest with the off-host export and the last database backup that verified.
3. Check database logs and owner-account sessions for the period; rotate the owner password and the digest key if either may be compromised.
4. Do not repair the records in place. Document the affected range and keep it classified as mismatched.

## Open decisions
- **Retention periods:** 180 days for audit events and 30 days for soft-deleted originals are proposals. They need approval with a stated legal basis before production use.
- **Historical partial purges:** digests partially purged by the retention function before migration `AuditWholeDigestRetention` are reported as `Purged` as a whole, so their surviving events cannot be re-verified. Options: classify them as legacy purged evidence (no code change), or add per-event commitments so surviving events can be verified. Installations created after that migration are not affected.
- **Scheduling:** whether retention runs as a scheduled maintenance job (owner credentials outside the API and worker) or stays a documented manual task.
- **Offline verifier:** a command-line verifier that checks exported digests with a given key, for rotation and incident work.
