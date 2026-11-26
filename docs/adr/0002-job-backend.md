# ADR 0002: Background job backend
Status: Proposed
Date: 2026-11-26
Issue: #2

## Context
DAM ingestion must tolerate retries and process restarts. Redis 7.4 offers RSALv2/SSPLv1; Redis 8 also offers AGPLv3. Valkey is BSD licensed. BullMQ documentation lists Valkey compatibility and a PostgreSQL backend.

## Options
- BullMQ + Valkey: permissive server license, separate service and persistence overhead.
- BullMQ + Redis 8: documented default integration, additional licensing review.
- PostgreSQL-backed job processing (including BullMQ PostgreSQL backend): fewer services, potential database contention and feature differences; verify pinned release.

## Proposed decision
Prefer BullMQ + supported Valkey 8.1 or 9.x after compatibility verification; keep PostgreSQL alternative if operational simplicity or measured workload warrants it. Do not use an unsupported Redis release to avoid licensing obligations.

## Consequences and validation
Use PostgreSQL as authoritative asset state; design transactional outbox or equivalent reconciliation with #5/#7. Test pinned client/server versions for enqueue, delayed retries, concurrent jobs, duplicate handling, worker termination/restart, persistence/restart, connection loss, and non-evicting queue configuration. Verify ACL/TLS and recovery assumptions. Record results and compare resource overhead. No compatibility test has been executed yet.

Sources: https://docs.bullmq.io/ ; https://redis.io/legal/licenses/ ; https://valkey.io/docs/topics/introduction/ ; https://valkey.io/topics/releases/
