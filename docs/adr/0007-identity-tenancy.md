---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/9
---

# ADR 0007: Identity and tenant isolation

## Context and proposed decision
Compare single organization, row-level multi-tenancy and dedicated tenant databases. Prefer single organization with catalog-scoped RBAC and organization keys; no SaaS isolation claim. Prefer OIDC server-side session with Keycloak candidate or existing provider. Validate authorization, session/CSRF, retention and audit controls.

## Consequences and acceptance
See the [consolidated architecture documents](../architecture/quality-mvp-roadmap.md) and the owning issue for test evidence, alternatives and residual risks. Review with affected issue owners; no approval or executed PoC is implied.
