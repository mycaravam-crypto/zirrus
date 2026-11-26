---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/2
---

# ADR 0002: Background job backend

## Context
DAM ingestion must tolerate retries and process restarts. The backend is .NET (ADR 0001). The [system architecture](../architecture/system-architecture.md) already makes a PostgreSQL outbox, written in the asset transaction, the source of truth for job intent. Dispatch is at least once, with deterministic idempotency keys and reconciliation. .NET hosted services alone are not durable.

## Considered options
- **PostgreSQL-backed queue consumed by a .NET worker**, either a permissive library with PostgreSQL persistence (e.g. Wolverine, Rebus; verify licenses) or a small in-house consumer using `FOR UPDATE SKIP LOCKED` with leases.
- **Separate broker** (Valkey or Redis with a .NET client, or RabbitMQ): extra stateful service, persistence and license review (Redis 7.4 is RSALv2/SSPLv1; Redis 8 adds AGPLv3; Valkey is BSD-3-Clause).
- **Hangfire with a PostgreSQL provider:** mature, but core is LGPL-3.0 with paid Pro features. Exception review under ADR 0003.

## Proposed decision
Use a PostgreSQL-backed queue consumed by the .NET worker, with **no separate broker in the MVP**.

- **Implementation:** a small in-house Npgsql consumer (`FOR UPDATE SKIP LOCKED`, leases, backoff, dead-letter state) behind an `IJobQueue` port, so a library can replace it locally later.
- **Next candidate:** Wolverine PostgreSQL transport (MIT; exact package and transitive license review required), adopted only if scheduled jobs, sagas or many message types are needed. Rebus remains an alternative.
- **Broker:** only if measured contention or throughput (#12, Q2) requires it.

See the [open-source evaluation](../architecture/open-source-dependencies.md#queue-decision-experiment). The spike validates the consumer; nothing is accepted yet.

## Consequences and validation
- One fewer stateful service to run, secure, back up and license-review.
- Queue load shares PostgreSQL; watch lock contention, vacuum and connection-pool use.
- Spike pass criteria: kill the worker mid-job 20 times; each job completes once logically; outbox re-dispatch recovers lost work; retries use exponential backoff; dead-lettered jobs are visible to operators.
- No compatibility test has been executed yet.

Sources: https://redis.io/legal/licenses/ ; https://valkey.io/docs/topics/introduction/ ; https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE
