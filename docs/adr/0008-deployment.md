---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/10
---

# ADR 0008: Container runtime and deployment

## Context and proposed decision
Prefer Linux Docker Engine + Compose v2 over Kubernetes for a single-host MVP. Podman best-effort pending compatibility; Docker Desktop optional subject to licensing. Caddy sole public ingress; private service networks, persistent volumes and isolated parser execution. Validate clean-host boot and recovery before acceptance.

## Consequences and acceptance
See the [consolidated architecture documents](../architecture/quality-mvp-roadmap.md) and the owning issue for test evidence, alternatives and residual risks. Review with affected issue owners; no approval or executed PoC is implied.
