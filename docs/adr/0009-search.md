---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/6
---

# ADR 0009: Search implementation

## Context and proposed decision
Prefer PostgreSQL FTS and indexed filters over a separate search cluster; benchmark permission-aware workload. Adopt dedicated search only if measured thresholds remain unmet after tuning.

## Consequences and acceptance
See the [consolidated architecture documents](../architecture/quality-mvp-roadmap.md) and the owning issue for test evidence, alternatives and residual risks. Review with affected issue owners; no approval or executed PoC is implied.
