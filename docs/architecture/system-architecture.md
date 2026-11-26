# System architecture — proposed MVP baseline
Issues: [#3](https://github.com/mycaravam-crypto/zirrus/issues/3), #5–#11. Confirmed constraints: TypeScript, Node.js, PostgreSQL. All other choices below are proposals for joint review.

## Context
A single organization manages digital assets. Administrator, editor and viewer use a browser; external identity provider authenticates users. External systems may consume a versioned REST API. No public asset access by default.

## Containers and trust boundaries
See [C4 views](../diagrams/system-views.md). Browser communicates over HTTPS with Caddy; same-origin SPA and API. NestJS modular monolith owns commands and queries; PostgreSQL is authoritative for catalog, permissions, metadata and job intent. Worker orchestrates ingestion via BullMQ/Valkey (pending ADR 0002 validation). Untrusted parsing runs in a restricted subprocess/container with no network; orchestrator needs narrowly scoped DB, queue and storage access. ClamAV signature update has separate controlled egress. Files are private and accessed through authorized API streaming. Single deployment initially; separately scale API and worker processes.

## Components and dependency rules
Identity/access, catalog, asset lifecycle, metadata schema, categories, search, audit, job dispatch, storage adapters. Collections, versioning and workflow are extension points only. Domain has no NestJS/ORM dependencies; application use cases depend on domain and ports; infrastructure implements ports; HTTP controllers call use cases. Cross-module interactions use published service contracts; no direct imports of another module's private repositories. Enforce with ESLint restricted imports/dependency graph tests. Shared contracts contain DTOs, not database entities. API validates permissions on every request; worker uses service identity, not user bearer tokens.

Synchronous: catalog changes, metadata edits, authorization, search, download admission. Asynchronous: scanning, extraction, renditions, reconciliation. Persist job intent in a PostgreSQL outbox in the asset transaction; dispatch at least once, use deterministic idempotency keys and reconcile lost jobs. Do not require exactly-once messaging.

Scale workers when queue age exceeds the agreed SLO over three representative load windows; scale API when latency/CPU saturation persists after query profiling. Extract services only when independently measured scaling or release/isolation needs justify added operational cost. Dedicated search requires #6 evidence.

## Key risks
Database/object atomicity, unsafe parsers, queue loss, permission leaks, cross-module coupling. Controls and acceptance evidence in the linked design documents. No architecture validation or load test has been executed.
