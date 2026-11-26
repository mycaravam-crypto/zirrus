---
status: accepted
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/9
---

# ADR 0007: Identity and tenant isolation

## Context and decision drivers
MVP serves one organization with multiple catalogs. Authorization must protect original files, metadata and all search derivatives.

## Considered options
- Single organization: simplest authorization and operations; not SaaS isolation.
- Shared database multi-tenancy: economical but cross-tenant leakage risk and more complex tests.
- Dedicated database per tenant: stronger isolation, greater provisioning and migration cost.

## Decision outcome
Prefer single organization with catalog-scoped RBAC and organization keys. ASP.NET Core OIDC, cookie and antiforgery handlers; Keycloak (Apache-2.0) candidate or existing compliant provider. No local passwords by default.

## Consequences
- Tenant-ready keys reduce future migration cost but are not a substitute for verified isolation.
- IdP availability and session lifecycle become dependencies.
- Retention and audit protection require separate explicit approval; avoid paid identity framework dependency.

## Acceptance evidence
Test OIDC login/logout, session rotation, CSRF, unauthorized direct IDs, counts, previews, ranges and cross-catalog role matrix. Confirm provider, retention and audit controls. See [security design](../architecture/security.md).

**Evidence (W2, #22; Keycloak verification #19):**
- **Role matrix (Q3):** integration tests against PostgreSQL cover anonymous 401 (not a redirect); admin, editor, viewer and outsider catalog listing; admin-only catalog creation and role management; 404 for unknown catalogs; and the composite key rejecting cross-organization memberships.
- **Sessions and CSRF:**
  - unsafe requests need a CSRF token, and another user's token is rejected;
  - logout ends the server-side session, so a copied cookie stops working;
  - sessions and keys work across two replicas;
  - the key ring is encrypted at rest, and Production refuses to start without the key.
- **Keycloak 26.8.2 end to end in CI** (`tools/ci/e2e-auth.py`, through Caddy): code + PKCE login, realm role `admin` mapped to the `roles` claim, CSRF, and RP-initiated logout ending the Keycloak session.

**Moved to W7 (#27):** retention and audit controls. **Moved to W8 (#28):** session rotation and token-lifetime review.
