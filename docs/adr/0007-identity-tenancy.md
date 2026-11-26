---
status: proposed
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

## Proposed decision
Prefer single organization with catalog-scoped RBAC and organization keys. ASP.NET Core OIDC, cookie and antiforgery handlers; Keycloak (Apache-2.0) candidate or existing compliant provider. No local passwords by default.

## Consequences
- Tenant-ready keys reduce future migration cost but are not a substitute for verified isolation.
- IdP availability and session lifecycle become dependencies.
- Retention and audit protection require separate explicit approval; avoid paid identity framework dependency.

## Acceptance evidence
Test OIDC login/logout, session rotation, CSRF, unauthorized direct IDs, counts, previews, ranges and cross-catalog role matrix. Confirm provider, retention and audit controls. See [security design](../architecture/security.md).

Validation and approving review pending; no executed PoC implied.
