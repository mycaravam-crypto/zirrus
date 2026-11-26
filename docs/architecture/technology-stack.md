# ZIRRUS technology evaluation

Status: Proposed | Issue: [#2](https://github.com/mycaravam-crypto/zirrus/issues/2) | Date: 2026-11-26

## Context and assumptions
Greenfield Apache-2.0 DAM. Assume a small team, web-first UI, self-hosted reference deployment and incremental delivery; actual team skills, budget and workload remain unconfirmed; TypeScript, Node.js and PostgreSQL are confirmed product constraints. No benchmark has been performed. Scores are engineering hypotheses, not measured results.

## Historical weighted decision matrix (superseded as selection mechanism)
Scale: 1 weak, 5 strong. Weighted total = sum(weight * score)/100. Team skill scores are neutral until confirmed. React is held constant across backend alternatives; frontend selection is a provisional assumption.

| Criterion | Weight | TypeScript/NestJS | Python/FastAPI | Java/Spring Boot |
|---|---:|---:|---:|---:|
| Team skills | 20 | 3 | 3 | 3 |
| Ecosystem maturity | 15 | 5 | 5 | 5 |
| LTS/support predictability | 15 | 4 | 4 | 5 |
| Licensing simplicity | 10 | 5 | 5 | 5 |
| Hiring potential (unverified) | 10 | 3 | 3 | 3 |
| Operational cost/complexity | 15 | 4 | 4 | 3 |
| Full-stack delivery efficiency | 15 | 5 | 4 | 3 |
| Weighted score | 100 | 4.10 | 3.95 | 3.80 |

Product owner decision (2026-11-26): TypeScript, Node.js and PostgreSQL are mandatory. The historical comparison is retained for traceability only; team skills no longer gate language/runtime/database selection. Identify onboarding and training needs instead. Frameworks and versions remain subject to review.

## Baseline dispositions
| Layer | Candidate | Disposition |
|---|---|---|
| Runtime | Node.js 26 LTS, 24 fallback | Validate dependency compatibility; pin patched version |
| UI | React, Vite, TypeScript, Tailwind, shadcn/ui | Adopt provisionally |
| UI data | TanStack Query/Table | Adopt provisionally |
| API | NestJS, REST, OpenAPI | Adopt provisionally |
| Data | PostgreSQL 18; Drizzle ORM candidate | Compare Drizzle, Prisma, TypeORM and Kysely + migrations in #4; validate JSONB/index support and migration/recovery with #10 |
| Search | PostgreSQL FTS, pg_trgm | Adopt provisionally; benchmark #6; defer OpenSearch |
| Storage | filesystem/S3 adapter | Adopt boundary; provider decision #5 |
| Jobs | BullMQ + Valkey | Preferred pending compatibility PoC; ADR 0002 |
| Media | Sharp/libvips, ExifTool, ClamAV | Validate format support, isolation and licenses |
| Video | FFmpeg | Defer unless #13 requires video |
| AI | Python workers | Defer until justified |
| IAM | OIDC; Keycloak candidate | Provider decision #9 |
| Deployment | Linux containers, Compose, Caddy | Validate #10 |
| Tooling | pnpm, GitHub Actions, ESLint, strict TypeScript | Detail #11 |
| Observability | OpenTelemetry | Detail #10 |
| QA | Vitest, Testing Library, Supertest, Testcontainers, Playwright, k6, axe-core, ZAP, Trivy, CycloneDX, Dependabot | Validate integration and license inventory #11/#12 |

## Support and validation
Node 26 LTS EOL April 2029; Node 24 EOL April 2028. PostgreSQL 18 support to November 2030. Valkey 8.1 security support to March 2030; 9.x candidate after compatibility testing. Framework/library support is not guaranteed by runtime LTS. Verify exact versions, peer dependencies, security status and upstream maintenance before lockfile creation; prefer supported releases with at least 12 months of planned support where published. Maintain quarterly review and upgrade ownership.

## Operational budget
No approved budget. See [ADR 0004](../adr/0004-operating-budget.md). Compare measured CPU/RAM/disk, backup storage, network transfer, CI minutes, support effort and license costs for a reference deployment from #12. Monthly cost model = compute + storage + backups + transfer + CI + operator hours × rate + licensing. No price or capacity commitment is made.

## Evidence and review
Official references: [Node release schedule](https://github.com/nodejs/Release), [PostgreSQL support](https://www.postgresql.org/support/versioning/), [Valkey support](https://valkey.io/topics/releases/), [BullMQ](https://docs.bullmq.io/), [Redis licensing](https://redis.io/legal/licenses/).
ADRs: [0001](../adr/0001-core-stack.md), [0002](../adr/0002-job-backend.md), [0003](../adr/0003-dependency-policy.md), [0004](../adr/0004-operating-budget.md). [License inventory](third-party-license-inventory.md). Pending onboarding assessment, PoCs, dependency checks and PR approval.
