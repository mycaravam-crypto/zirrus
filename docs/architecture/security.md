# Security and privacy architecture
Issue #9. Proposed; ISO/IEC 27001:2022 alignment only.

## Identity and authorization
Single organization MVP, catalog-scoped membership; organization ID retained for future isolation, not a multi-tenant SaaS claim. OIDC authorization code + PKCE, server-side session, Secure HttpOnly SameSite cookie, CSRF token/origin checks on unsafe methods, rotate session at login; logout invalidates session. Keycloak candidate; no local passwords unless ADR changes. Deny by default and apply authorization at use case and query boundaries. Admin role organization-scoped; editor/viewer catalog-scoped. Service identities least privilege. No trust in browser role assertions.
| Action | Admin | Editor (granted catalog) | Viewer (granted catalog) |
|---|---|---|---|
| Manage catalogs/categories/role assignments | Yes | No | No |
| Read active asset, preview, search | Yes | Yes | Yes |
| Upload/edit/assign categories/delete/retry | Yes | Yes | No |
| Read audit | Yes | No | No |
All ungranted catalogs denied except organization administrator; test direct ID, search/counts, download/range and rendition access; use scoped DB queries and composite keys. Never disclose existence through errors or timing where practical.

## Threats and controls
| Threat | Control | Verification |
|---|---|---|
| Spoofing/session theft | OIDC, TLS, secure cookies, CSRF, session expiry | login/logout/replay tests |
| Tampering/asset corruption | SHA-256, immutable originals, revision checks | corruption and stale edit tests |
| Repudiation | append-only audit, separate privileges, integrity monitoring | audit mutation tests |
| Information disclosure | scoped queries and private storage, log redaction | cross-catalog tests |
| Denial of service | rate/size limits, queue backpressure, parser isolation | overload and malformed files |
| Elevation/SSRF | deny default, no referenced URL fetch, no parser network | negative role and network tests |

Audit events: ID, actor, action, resource, outcome, UTC timestamp, correlation ID. Application DB role insert-only on audit table; restricted maintenance role handles retention. Proposed periodic signed digest exported to separate restricted backup location; assess privileged operator risk (not absolute immutability). No secrets/raw tokens or unnecessary personal data. Protect logs and define incident access. Proposed audit retention 180 days, soft-deleted originals 30 days; confirm policy and legal basis, document deletion requests and backup expiry. TLS, secrets outside source, patching and SBOM; no public storage endpoints. Annex A mapping: 5.15–5.18 access/identity; 8.2/8.3 privileged access/restriction; 8.7 malware; 8.9 configuration; 8.13 backups; 8.15/8.16 logging/monitoring; 8.24 cryptography; 8.25/8.28/8.29 secure development/coding/testing. Residual risks: malicious privileged operator, parser zero-day, IdP outage; review before release. No compliance certification implied.
