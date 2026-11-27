# Security and privacy architecture
Issue #9. Proposed; ISO/IEC 27001:2022 alignment only.

## Identity and authorization
Single organization MVP, catalog-scoped membership; organization ID retained for future isolation, not a multi-tenant SaaS claim.

- **Login:** OIDC authorization code + PKCE through the ASP.NET Core OpenID Connect handler; a server-side session in a Secure, HttpOnly, SameSite cookie (cookie authentication handler). Rotate the session at login; logout invalidates it.
- **CSRF:** ASP.NET Core antiforgery tokens plus origin checks on unsafe methods.
- **Provider:** Keycloak candidate; no local passwords unless an ADR changes this.
- **Authorization:** deny by default, using ASP.NET Core policy-based authorization at use-case and query boundaries. Admin role is organization-scoped; editor/viewer are catalog-scoped. Service identities have least privilege. Browser role assertions are never trusted.

| Action | Admin | Editor (granted catalog) | Viewer (granted catalog) |
|---|---|---|---|
| Manage catalogs/categories/role assignments | Yes | No | No |
| Read active asset, preview, search | Yes | Yes | Yes |
| Upload/edit/assign categories/delete/retry | Yes | Yes | No |
| Read audit | Yes | No | No |

All ungranted catalogs are denied except for the organization administrator. Test direct ID, search/counts, download/range and rendition access; use scoped database queries and composite keys. Never disclose existence through errors or timing where practical.

## Threats and controls
| Threat | Control | Verification |
|---|---|---|
| Spoofing/session theft | OIDC, TLS, secure cookies, CSRF, session expiry | login/logout/replay tests |
| Tampering/asset corruption | SHA-256, immutable originals, revision checks | corruption and stale edit tests |
| Repudiation | append-only audit, separate privileges, integrity monitoring | audit mutation tests |
| Information disclosure | scoped queries and private storage, log redaction | cross-catalog tests |
| Denial of service | rate/size limits, queue backpressure, parser isolation | overload and malformed files |
| Elevation/SSRF | deny default, no referenced URL fetch, no parser network | negative role and network tests |

## Audit and retention
- **Events:** ID, actor, action, resource, outcome, UTC timestamp, correlation ID, and short non-secret detail (for example a granted role).
- **Recorded actions** (`AuditActions`): sign-in and sign-out; upload, metadata change, retry and soft delete of assets; catalog creation; role grant and revoke; schema creation; category creation, assignment and removal; authenticated API access denied with 403 or resource-hiding 404.
  - Each entry is staged in the request's unit of work and committed **in the same transaction** as the change, so a rejected or rolled-back change leaves no entry and a committed change always has one.
  - Authenticated API requests ending in 403 or 404 are recorded independently after the response. The event contains the actor, route template, method, catalog/resource IDs when available, outcome and correlation ID; it never stores request bodies, query values or raw resource paths. Anonymous 401 responses are not attributed to a user and are not recorded.
- **Integrity** (implemented, AuditHardening migration):
  - **Roles:** the API and worker connect as `zirrus_app`. It has DML on application tables but only `INSERT` and `SELECT` on `audit_events` and `audit_digests`, no access to the purge-evidence ledger or migration history, and no right to run the purge. Migrations and the `db-roles` deployment job use the owner account.
  - **Append-only for every role:** triggers reject `UPDATE`, `DELETE` and `TRUNCATE` on `audit_events`, `audit_digests` and the `audit_purged_events` evidence ledger, including for the owner. The only deletion path for events is `audit_purge(before)`, a `SECURITY DEFINER` function the maintenance (owner) role runs for retention.
  - **Signed digests:** the worker seals new events (after a 5-minute safety lag) into a digest. The digest is SHA-256 over the previous digest and a canonical line per event, signed with HMAC-SHA256 under `Audit:DigestKey`. It is stored in `audit_digests` and also written to `Audit:ExportDirectory`, which should be backed up off-host.
  - **Verification:** each run re-verifies recent digests. A changed, inserted or removed sealed event, a broken chain or a wrong signature is logged as critical. The retention function records each deleted event sequence and ID in the append-only `audit_purged_events` ledger before deletion. Retention only removes a complete digest range after every event in that range has expired; it never partially purges a sealed digest. Unsealed events remain until a digest is created. A fully purged range is distinguished using its exact sequence evidence and original event count; a missing event without matching evidence is a mismatch. The ledger contains identifiers and timestamps, not the event payload.
  - **Limits:** a privileged operator with the owner account and the digest key could rewrite both. Keeping a copy of the exported digests and the key off-host limits that, but absolute immutability is not claimed.
- **Evidence (Q10):** `AuditTests` and `AuditDigestTests` (PostgreSQL) cover:
  - writes through the API are audited;
  - the owner cannot change audit records;
  - `zirrus_app` cannot update, delete, truncate or purge;
  - a tampered sealed record is detected;
  - missing internal events, reordered events and truncated digest ranges are detected;\n  - a fully expired digest range is purged as a unit, while a range containing any unexpired event remains intact and verifiable;
  - authenticated 403 and resource-hiding 404 responses are audited without request payloads.
- **Content:** no secrets, raw tokens or unnecessary personal data. Protect logs and define incident access.
- **Retention:** proposed 180 days for audit and 30 days for soft-deleted originals. Confirm policy and legal basis; document deletion requests and backup expiry.

Baseline controls: TLS, secrets outside source, patching and SBOM; no public storage endpoints.

Annex A mapping: 5.15–5.18 access/identity; 8.2/8.3 privileged access/restriction; 8.7 malware; 8.9 configuration; 8.13 backups; 8.15/8.16 logging/monitoring; 8.24 cryptography; 8.25/8.28/8.29 secure development/coding/testing.

Residual risks: malicious privileged operator, parser zero-day, IdP outage; review before release. No compliance certification implied.
