---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/10
---

# ADR 0008: Container runtime and deployment

## Context and decision drivers
Self-hosted reference deployment should be reproducible and require minimal operating effort.

## Considered options
- Docker Engine + Compose: low setup overhead; single-host failure domain.
- Podman: permissive alternative; Compose behavior and rootless compatibility require testing.
- Kubernetes: orchestration and multi-host scheduling; disproportionate complexity for this MVP.

## Proposed decision
Prefer Linux Docker Engine and Compose v2. Caddy sole public ingress, private networks, pinned images, non-root processes, isolated parsers and PostgreSQL-backed jobs. Docker Desktop optional under its separate terms; Kubernetes deferred.

## Consequences
- Document volume ownership, backup coverage and health checks.
- Single-host downtime remains possible; enforce RPO/RTO and recovery drills.
- Revisit orchestration when measured scaling or multi-host availability requirements cannot be met.

## Acceptance evidence
Validate clean-host Compose boot, health, secret handling, network isolation, backup/restore, failed migration recovery and supported architectures. See [deployment](../architecture/deployment-delivery.md#runtime) and [recovery](../architecture/deployment-delivery.md#backuprecovery).

Validation and approving review pending; no executed PoC implied.
