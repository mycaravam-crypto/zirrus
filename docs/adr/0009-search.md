---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/6
---

# ADR 0009: Search implementation

## Context and decision drivers
Search must respect catalog permissions and support flexible metadata at the proposed workload.

## Considered options
- PostgreSQL FTS with GIN and relational/JSONB indexes: transactional consistency and fewer services; advanced ranking/facets require tuning.
- OpenSearch: advanced search capabilities and independent scale; extra infrastructure and asynchronous consistency.
- Unindexed SQL substring search: simple implementation; unacceptable scaling risk for broad queries.

## Proposed decision
Prefer PostgreSQL FTS and indexed filters. Apply authorization before results, counts, facets and pagination. Use English/German configurations and stable cursors. pg_trgm where justified.

## Consequences
- Search and ingest share PostgreSQL resources; monitor contention and query plans.
- No external index lag in MVP; search-specific scaling remains limited.
- Adopt dedicated search only after repeated misses of agreed thresholds following tuning.

## Acceptance evidence
Benchmark Q1 with seeded corpus, mixed grants, cold/warm cache and concurrent clients; inspect plans and verify no unauthorized result/count. See [search design](../architecture/data-storage-search.md#search) and [quality gates](../architecture/quality-mvp-roadmap.md).

Validation and approving review pending; no executed PoC implied.
