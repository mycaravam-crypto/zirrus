# System architecture — proposed MVP baseline
Issues: [#3](https://github.com/mycaravam-crypto/zirrus/issues/3), #5–#11. Decided: C# / .NET 10 / ASP.NET Core backend, React/TypeScript frontend, PostgreSQL ([ADR 0001](../adr/0001-core-stack.md)). All other choices below are proposals for joint review.

## Context
A single organization manages digital assets. Administrator, editor and viewer use a browser; external identity provider authenticates users. External systems may consume a versioned REST API. No public asset access by default.

## Containers and trust boundaries
See [C4 views](../diagrams/system-views.md). The browser communicates over HTTPS with Caddy; SPA and API are same-origin.

- The ASP.NET Core modular monolith owns commands and queries.
- PostgreSQL is authoritative for catalog, permissions, metadata and job intent, and also holds the job queue ([ADR 0002](../adr/0002-job-backend.md)).
- The .NET worker claims jobs from PostgreSQL and orchestrates ingestion. Untrusted parsing runs in a restricted subprocess/container with no network.
- The worker has narrowly scoped database and storage access. ClamAV signature updates have separate controlled egress.
- Files are private and accessed through authorized API streaming. Single deployment initially; API and worker scale separately.

## Components and dependency rules
Modules: identity/access, catalog, asset lifecycle, metadata schema, categories, search, audit, job dispatch, storage adapters. Collections, versioning and workflow are extension points only.

- Layers are separate projects: `Zirrus.Domain` (no ASP.NET Core or EF Core references), `Zirrus.Application` (use cases and ports), `Zirrus.Infrastructure` (EF Core, storage, queue), `Zirrus.Api` (HTTP endpoints) and `Zirrus.Worker`.
- Project references enforce the direction at compile time. Architecture tests and the DocWizz layer rules (ARCH-001) catch violations in CI.
- Cross-module calls use published application contracts; no direct use of another module's persistence.
- API DTOs are not EF Core entities. The API validates permissions on every request; the worker uses a service identity, not user tokens.

Synchronous: catalog changes, metadata edits, authorization, search, download admission. Asynchronous: scanning, extraction, renditions, reconciliation. Persist job intent in a PostgreSQL outbox in the asset transaction; dispatch at least once, use deterministic idempotency keys and reconcile lost jobs. Do not require exactly-once messaging.

Scale workers when queue age exceeds the agreed SLO over three representative load windows; scale the API when latency/CPU saturation persists after query profiling. Extract services only when independently measured scaling or release/isolation needs justify added operational cost. Dedicated search requires #6 evidence.

## Key risks
Database/object atomicity, unsafe parsers, queue contention in PostgreSQL, permission leaks, cross-module coupling. Controls and acceptance evidence are in the linked design documents. No architecture validation or load test has been executed.
