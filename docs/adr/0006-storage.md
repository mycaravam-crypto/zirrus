---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/5
---

# ADR 0006: Asset storage and lifecycle

## Context and proposed decision
Compare managed filesystem, managed S3 and referenced NAS. Prefer managed filesystem for initial single-host deployment with opaque keys and private storage adapter; test S3-compatible round trip as portability evidence. Defer NAS reference mode and versions unless #13 requires them. Soft delete and 30-day purge proposed subject to retention review.

## Consequences and acceptance
See the [consolidated architecture documents](../architecture/quality-mvp-roadmap.md) and the owning issue for test evidence, alternatives and residual risks. Review with affected issue owners; no approval or executed PoC is implied.
