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
- **Events:** ID, actor, action, resource, outcome, UTC timestamp, correlation ID.
- **Integrity:** the application database role is insert-only on the audit table; a restricted maintenance role handles retention. A periodic signed digest is exported to a separate restricted backup location. Privileged-operator risk is assessed; absolute immutability is not claimed.
- **Content:** no secrets, raw tokens or unnecessary personal data. Protect logs and define incident access.
- **Retention:** proposed 180 days for audit and 30 days for soft-deleted originals. Confirm policy and legal basis; document deletion requests and backup expiry.

Baseline controls: TLS, secrets outside source, patching and SBOM; no public storage endpoints.

Annex A mapping: 5.15–5.18 access/identity; 8.2/8.3 privileged access/restriction; 8.7 malware; 8.9 configuration; 8.13 backups; 8.15/8.16 logging/monitoring; 8.24 cryptography; 8.25/8.28/8.29 secure development/coding/testing.

Residual risks: malicious privileged operator, parser zero-day, IdP outage; review before release. No compliance certification implied.
