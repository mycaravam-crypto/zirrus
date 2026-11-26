---
status: proposed
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

## Proposed decision
Use **EF Core with the Npgsql provider**.

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

No PoC has been executed yet.

Sources: https://www.npgsql.org/efcore/mapping/json.html ; https://www.npgsql.org/efcore/mapping/full-text-search.html
