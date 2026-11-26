---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/4
---

# ADR 0005: Persistence and migrations

## Context and proposed decision
Compare Drizzle, Prisma, TypeORM and Kysely with SQL migrations. Prefer Drizzle for typed SQL and explicit PostgreSQL features; Prisma offers generated client but specialized SQL may require escape hatches; TypeORM offers entity mapping with added runtime conventions; Kysely provides typed query building with more manual mapping. Validate JSONB, GIN, schema evolution and recovery before acceptance. PostgreSQL is confirmed.

## Consequences and acceptance
See the [consolidated architecture documents](../architecture/quality-mvp-roadmap.md) and the owning issue for test evidence, alternatives and residual risks. Review with affected issue owners; no approval or executed PoC is implied.
