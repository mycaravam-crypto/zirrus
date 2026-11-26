---
status: accepted
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/4
---

# ADR 0005: Persistence and migrations

## Context and decision drivers
The backend is .NET (ADR 0001) on PostgreSQL. The [data model](../architecture/data-storage-search.md) needs relational integrity with composite keys, typed JSONB metadata with GIN and expression indexes, PostgreSQL full-text search, and optimistic concurrency. Migrations must run once as a one-off container before compatible application startup (#10).

## Considered options
- **EF Core + Npgsql provider:** LINQ and change tracking, JSONB mapping, full-text search translation, generated migrations and migration bundles; raw SQL for specialized queries.
- **Dapper + DbUp (or another SQL migration runner):** explicit SQL, little abstraction, more hand-written mapping and no model-driven migrations.
- **linq2db:** typed SQL-first LINQ, smaller ecosystem.

## Decision outcome
Use **EF Core with the Npgsql provider** (10.x packages while on .NET 10; the 11.0 packages target only `net11.0`).

- Migrations are generated, reviewed as SQL in the pull request, and shipped as an EF Core migration bundle run by a one-off container.
- Use raw parameterized SQL where EF Core translation is inadequate (e.g. permission-scoped search with facets).
- Domain projects do not reference EF Core; mapping lives in infrastructure.

## Consequences
- **Good:** one mature, first-party data stack; concurrency tokens map to the revision checks; JSONB and full-text search are documented provider features.
- **Bad:** LINQ translation must be checked for complex queries; generated migrations still need review.

## Acceptance evidence
Validate in the metadata-query PoC (#4) on the #12 dataset:
- JSONB filters and GIN/expression index use (EXPLAIN ANALYZE);
- composite foreign keys;
- concurrency conflicts (412 path);
- migration bundle apply and failed-migration recovery (#10, Q9).

**Spike result:** EF Core 10.0.15 + Npgsql 10.0.4 on 100,000 rows. The JSONB filter translated to `@>` and used the GIN index (3.6 ms). A stale edit raised `DbUpdateConcurrencyException`. The migration bundle applied to an empty database; a faulty migration failed atomically. See [spike results](../architecture/spike-results.md#1c-persistence-adr-0005).

**Moved to implementation checks:** composite foreign keys (#4) and the full failed-migration recovery drill (#10, Q9). Mark `**/Migrations/*.cs` as generated code so analyzers do not fail builds.

Sources: https://www.npgsql.org/efcore/mapping/json.html ; https://www.npgsql.org/efcore/mapping/full-text-search.html
