# ZIRRUS technology evaluation

Status: Backend decided ([ADR 0001](../adr/0001-core-stack.md)); other dispositions proposed | Issue: [#2](https://github.com/mycaravam-crypto/zirrus/issues/2) | Date: 2026-11-26

## Context and assumptions
Greenfield Apache-2.0 DAM. Assume a small team, web-first UI, self-hosted reference deployment and incremental delivery; budget and workload remain unconfirmed. Decided: C# / .NET 10 LTS / ASP.NET Core backend, React/TypeScript frontend, PostgreSQL (REV-BE-01, see the [backend reassessment](backend-reassessment.md)). No benchmark has been performed.

## Historical weighted decision matrix (superseded)
Retained for traceability only. It compared backend languages before .NET was considered; the selection was made in the [backend reassessment](backend-reassessment.md). Scale 1 weak, 5 strong; weighted total = sum(weight × score)/100.

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

## Baseline dispositions
| Layer | Candidate | Disposition |
|---|---|---|
| Backend runtime | .NET 10 LTS (C#) | Decided (ADR 0001); pin patched SDK/runtime in `global.json` and images |
| API | ASP.NET Core minimal APIs/controllers, built-in OpenAPI | Decided (ADR 0001) |
| Auth | ASP.NET Core OpenID Connect + cookie handlers, antiforgery, policy authorization | Adopt; provider decision #9 (Keycloak candidate) |
| Data | PostgreSQL 18; EF Core + Npgsql, migration bundles | Proposed ADR 0005; validate in #4 PoC |
| Jobs | .NET worker on a PostgreSQL-backed queue, no broker | Proposed ADR 0002; validate in spike |
| Media | NetVips/libvips, ExifTool, ClamAV | Validate format support, isolation and licenses |
| Frontend | React, Vite, TypeScript, Tailwind, shadcn/ui; Node.js 24/26 LTS as build tooling only | Adopt provisionally |
| UI data | TanStack Query/Table; TS client generated from OpenAPI | Adopt provisionally |
| Search | PostgreSQL FTS, pg_trgm | Adopt provisionally; benchmark #6; defer OpenSearch |
| Storage | filesystem/S3 adapter | Adopt boundary; provider decision #5 |
| Video | FFmpeg | Defer unless #13 requires video |
| AI | Python workers | Defer until justified |
| Deployment | Linux containers, Compose, Caddy | Validate #10 |
| Tooling | dotnet SDK with analyzers and central package versions; pnpm, ESLint and strict TypeScript for web; GitHub Actions; DocWizz | Detail #11 |
| Observability | OpenTelemetry .NET and browser SDKs | Detail #10 |
| QA | xUnit, WebApplicationFactory, Testcontainers for .NET; Vitest and Testing Library for web; Playwright, k6, axe-core, ZAP, Trivy, CycloneDX, Dependabot | Validate integration and license inventory #11/#12 |

## Support and validation
- **.NET 10 LTS:** support ends 2028-11-14. Plan the move to the next LTS (expected November 2027).
- **Node.js** (frontend build only): Node 26 LTS EOL April 2029; Node 24 EOL April 2028.
- **PostgreSQL 18:** supported to November 2030.

Framework and library support is not guaranteed by runtime LTS. Verify exact versions, transitive dependencies, security status and upstream maintenance before creating lock files. Prefer supported releases with at least 12 months of planned support where published. Maintain quarterly review and upgrade ownership.

## Operational budget
No approved budget. See [ADR 0004](../adr/0004-operating-budget.md). Compare measured CPU/RAM/disk, backup storage, network transfer, CI minutes, support effort and license costs for a reference deployment from #12. Monthly cost model = compute + storage + backups + transfer + CI + operator hours × rate + licensing. No price or capacity commitment is made.

## Evidence and review
Official references: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), [Node release schedule](https://github.com/nodejs/Release), [PostgreSQL support](https://www.postgresql.org/support/versioning/), [Npgsql EF Core JSON mapping](https://www.npgsql.org/efcore/mapping/json.html).
ADRs: [0001](../adr/0001-core-stack.md), [0002](../adr/0002-job-backend.md), [0003](../adr/0003-dependency-policy.md), [0004](../adr/0004-operating-budget.md), [0005](../adr/0005-persistence.md). [License inventory](third-party-license-inventory.md). Pending PoCs, dependency checks and PR approval.
