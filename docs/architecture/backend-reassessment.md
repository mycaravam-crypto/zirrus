# Backend reassessment: Node.js versus ASP.NET Core

Status: Proposed for review | Date: 2026-11-26 | Issues: #2, #3, #4, #7, #8, #9, #10, #11, #12

## Decision context
The product owner previously selected TypeScript, Node.js and PostgreSQL and has now requested a brief ASP.NET Core evaluation before implementation. PostgreSQL and React/TypeScript frontend remain common constraints. Node.js is the current baseline, not superseded by this evaluation. No production code exists; resolve the backend choice before accepting ADR 0001 and starting the walking skeleton.

## Alternatives
| Concern | A: TypeScript / Node.js 26 / NestJS | B: C# / .NET 10 LTS / ASP.NET Core |
|---|---|---|
| API | Controllers, guards, OpenAPI | Controllers or minimal APIs, policy authorization, OpenAPI |
| Persistence | Drizzle candidate, SQL migrations | EF Core/Npgsql candidate, SQL for specialized queries |
| Jobs | BullMQ/Valkey candidate | Worker Service plus durable PostgreSQL queue candidate (e.g. pg-boss is Node-only; evaluate a .NET implementation such as Hangfire PostgreSQL provider or a transactional outbox consumer) |
| Media | Sharp/libvips, ExifTool, ClamAV | NetVips/libvips, ExifTool, ClamAV |
| Contracts | Shared TS types plus runtime validation | OpenAPI-generated TS client plus runtime validation |
| Tests | Vitest/Supertest/Testcontainers | xUnit/WebApplicationFactory/Testcontainers |
| Deployment | Linux containers / Compose | Linux containers / Compose |

.NET hosted services alone do not provide durable jobs. Validate retry, leases, idempotency, persistence, and licensing of the chosen queue independently. Do not introduce both server runtimes without evidence.

## Assessment
ASP.NET Core offers an integrated server framework, mature static typing, policy-based authorization and strong database integration. Npgsql supports JSONB and PostgreSQL full-text search. Node.js offers a single application language, existing design alignment, strong media tooling and simpler shared developer tooling. Both support streaming, asynchronous I/O and modular architecture. CPU-heavy parsers must remain isolated regardless of runtime. No performance winner is established without an equivalent workload benchmark.

| Criterion | Weight | Node | .NET |
|---|---:|---:|---:|
| Team proficiency (unknown, neutral) | 25 | 3 | 3 |
| MVP delivery and current design fit | 25 | 5 | 4 |
| Maintainability and framework integration | 20 | 4 | 5 |
| Security engineering support | 10 | 4 | 4 |
| PostgreSQL and media ecosystem | 10 | 5 | 5 |
| Operational simplicity | 10 | 4 | 4 |
| Weighted total / 5 | 100 | 4.10 | 4.05 |

Scores are hypotheses, not benchmarks. One point of team proficiency changes the total by 0.25. With equal skills, the difference is negligible. Prefer Node.js for continuity unless C# expertise or evidence justifies the alternative. Both are technically viable.

## Support evidence
.NET 10 LTS support ends 2028-11-14; Node 26 LTS EOL April 2029 according to current upstream schedules. Keep patched versions.
- https://dotnet.microsoft.com/en-us/platform/support/policy
- https://nodejs.org/en/blog/announcements/evolving-the-nodejs-release-schedule
- https://www.npgsql.org/efcore/mapping/json.html
- https://www.npgsql.org/efcore/mapping/full-text-search.html

## Review gate
Confirm Node.js or .NET explicitly. If uncertain, timebox an equivalent vertical slice (upload stream, SHA-256, PostgreSQL metadata, permission-aware search, durable job retry, thumbnail) in each candidate; record development effort, p95, memory, dependency licensing and failure behavior using the same dataset/host. No PoC executed. If .NET is selected, revise ADRs 0001/0002/0005, stack inventory, diagrams, repository/CI and test strategy together; keep domain, API behavior, PostgreSQL, storage, security objectives and quality thresholds. If Node is retained, record rejection rationale and proceed with existing baseline. No merge or implementation authorization until the review gate is satisfied.
