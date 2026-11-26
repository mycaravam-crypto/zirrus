# ADR 0001: Core application stack
Status: Proposed
Date: 2026-11-26
Issue: #2

## Context and decision drivers
Maintainable DAM with typed contracts, flexible metadata, asynchronous media processing, low operational burden and open standards. Team skills and workloads are unknown.

## Options
TypeScript/NestJS + React; Python/FastAPI + React; Java/Spring Boot + Angular. See [weighted evaluation](../architecture/technology-stack.md).

## Proposed decision
Use TypeScript-first React/Vite frontend and NestJS modular API, PostgreSQL relational/JSONB model, REST/OpenAPI and separately deployable workers. Keep provider boundaries for storage and job dispatch; permit Python workers only where evidence justifies them. Adopt the provisional baseline dispositions in the evaluation.

## Consequences
Shared language and contracts reduce context switching. Native media libraries still require isolated workers, resource limits and compatibility testing. Modular monolith avoids distributed business transactions but requires enforced module boundaries. PostgreSQL search is initial default, not a guaranteed capacity claim.

## Acceptance evidence
Confirm team skills and support horizon; compile a minimal API/UI contract and database migration against pinned versions in a bounded PoC or approved walking skeleton; record license inventory and trade-offs. Review via PR; no production bootstrap before #2 and #11 approval.
