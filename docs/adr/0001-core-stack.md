---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/2
---

# ADR 0001: Core application stack

## Context and decision drivers
Maintainable DAM with typed contracts, flexible metadata, asynchronous media processing, low operational burden and open standards. Team skills and workloads are unknown. Product owner confirmed TypeScript, Node.js and PostgreSQL on 2026-11-26; these are binding constraints.

## Historical options (not candidates for the primary stack)
TypeScript/NestJS + React; Python/FastAPI + React; Java/Spring Boot + React. React is held constant as a provisional frontend assumption, not a separately scored decision. See [weighted evaluation](../architecture/technology-stack.md).

## Confirmed constraints and proposed implementation
Use TypeScript-first React/Vite frontend and NestJS modular API, PostgreSQL relational/JSONB model, REST/OpenAPI and separately deployable workers. Keep provider boundaries for storage and job dispatch; permit Python workers only where evidence justifies them. Adopt the provisional baseline dispositions in the evaluation.

## Consequences
Shared language and contracts reduce context switching. Native media libraries still require isolated workers, resource limits and compatibility testing. Modular monolith avoids distributed business transactions but requires enforced module boundaries. PostgreSQL search is initial default, not a guaranteed capacity claim.

## Acceptance evidence
The product owner has settled the primary language, runtime and database; no further comparison is required. Assess onboarding needs without reopening the decision. Confirm framework choice and support horizon; compile a minimal API/UI contract and database migration against pinned versions in a bounded design PoC before approval (not production bootstrap); record license inventory and trade-offs. Review via PR; no production bootstrap before #2 and #11 approval.
