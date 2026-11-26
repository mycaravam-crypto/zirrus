---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/5
---

# ADR 0006: Asset storage and lifecycle

## Context and decision drivers
Single-host MVP needs private, recoverable original storage without forcing a proprietary object service.

## Considered options
- Managed filesystem: few dependencies, atomic rename on one filesystem, straightforward backup; host locality limits scale.
- S3-compatible storage: independent scaling and portability; extra service, consistency and license review.
- Referenced NAS: avoids copying legacy originals but introduces external permissions, availability and reconciliation complexity.

## Proposed decision
Prefer managed filesystem through a storage port. Use opaque keys, quarantine, checksums and authorized streaming. Keep S3 adapter boundary; defer NAS references and binary versions unless #13 requires them. Proposed 30-day purge subject to retention approval.

## Consequences
- Lower operational burden; filesystem capacity and backup become explicit constraints.
- Database and filesystem writes cannot share an ACID transaction; reconcile incomplete operations and deny access until active.
- Do not mandate a particular S3 server for production; evaluate provider and version license before adoption.
- The test profile uses SeaweedFS (Apache-2.0) for S3-compatible round trips; MinIO (AGPL-3.0) only as a recorded test-tool exception (ADR 0003). The adapter client uses the AWS SDK for .NET (Apache-2.0).

## Acceptance evidence
Round-trip filesystem and S3-compatible test with checksums, ranges, interrupted writes, retries and recovery. Verify missing/orphan handling, catalog-scoped duplicate behavior and deletion denial. See [storage design](../architecture/data-storage-search.md#storage).

Validation and approving review pending; no executed PoC implied.
