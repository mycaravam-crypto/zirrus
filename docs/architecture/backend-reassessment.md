# Backend reassessment: Node.js versus ASP.NET Core

Status: Decided — option B (REV-BE-01, product owner, 2026-11-26) | Issues: #2, #3, #4, #7, #8, #9, #10, #11, #12

## Decision context
The product owner first selected TypeScript, Node.js and PostgreSQL, then requested an ASP.NET Core evaluation before implementation. PostgreSQL and the React/TypeScript frontend were common constraints. No production code existed, so switching only required revising design documents.

**Outcome:** C# / .NET 10 LTS / ASP.NET Core for the backend ([ADR 0001](../adr/0001-core-stack.md)). Option A (Node.js/NestJS) is rejected and option C (comparative PoC) is not needed; rationale below.

## Alternatives
| Concern | A: TypeScript / Node.js 26 / NestJS | B: C# / .NET 10 LTS / ASP.NET Core (chosen) |
|---|---|---|
| API | Controllers, guards, OpenAPI | Minimal APIs or controllers, policy authorization, built-in OpenAPI |
| Auth | Passport, openid-client, session store, separate CSRF package | First-party OpenID Connect and cookie handlers, antiforgery |
| Persistence | Drizzle candidate, SQL migrations | EF Core + Npgsql, migration bundles ([ADR 0005](../adr/0005-persistence.md)) |
| Jobs | BullMQ, requires Valkey/Redis | .NET worker on a PostgreSQL-backed queue, no broker ([ADR 0002](../adr/0002-job-backend.md)) |
| Media | Sharp/libvips, ExifTool, ClamAV | NetVips or SkiaSharp (Magick.NET for extended formats), MetadataExtractor / ExifTool, ClamAV |
| Contracts | Shared TS types plus runtime validation | TS client generated from the CI-checked OpenAPI document |
| Tests | Vitest/Supertest/Testcontainers | xUnit, WebApplicationFactory, Testcontainers for .NET |
| DocWizz gate | Frontend only; no NestJS endpoint detection | Endpoints, authorization, DI and `DbContext` access modeled |
| Deployment | Linux containers / Compose | Linux containers / Compose, one fewer stateful service |

## Assessment
Both stacks are technically viable at MVP volumes; no performance winner is claimed without an equivalent benchmark. CPU-heavy parsers stay isolated regardless of runtime. B was preferred because it fits this design and project:

- The [security design](security.md) maps onto ASP.NET Core built-ins instead of assembled packages.
- EF Core + Npgsql settles the open ORM question with a mature, first-party stack.
- The PostgreSQL outbox already planned can drive the worker directly, removing Valkey.
- The documentation gate (#16) analyzes ASP.NET Core in depth, and its default C# layer rules match the planned `src/Zirrus.<Layer>` projects (checked locally with DocWizz v0.7.143: layers detected, a domain → infrastructure call flagged as ARCH-001, minimal-API routes detected).
- The project owner's own tooling (DocWizz) is a substantial .NET 10 codebase, which is evidence of C# proficiency.

| Criterion | Weight | Node | .NET |
|---|---:|---:|---:|
| Team proficiency (evidence: owner's .NET tooling; confirm with backend team) | 25 | 3 | 4 |
| MVP delivery (design revision is a one-off cost, ~1 day) | 20 | 4 | 4 |
| Maintainability and framework integration | 20 | 4 | 5 |
| Security engineering support (built-in vs. assembled auth) | 10 | 4 | 5 |
| PostgreSQL and media ecosystem | 10 | 5 | 5 |
| Operational simplicity (Node: one toolchain + broker; .NET: two toolchains, no broker) | 10 | 4 | 4 |
| Documentation gate fit (DocWizz) | 5 | 2 | 5 |
| Weighted total / 5 | 100 | 3.75 | 4.45 |

Scores remain engineering judgments, not benchmarks. Compared with the earlier matrix, only the rows argued above changed; "current design fit" was removed as a sunk cost. B still leads with equal team proficiency (4.20 vs 3.75).

## Costs and mitigations
| Cost / risk | Mitigation |
|---|---|
| Two toolchains (dotnet + pnpm); no shared TS types | Generate the TS client from OpenAPI; central package versions; separate CI jobs |
| .NET library licensing drift | Review list in [ADR 0003](../adr/0003-dependency-policy.md) |
| NetVips less turnkey than Sharp | Same libvips; verify native packages in the spike; parsers stay isolated |
| .NET 10 LTS support ends 2028-11-14 | Plan the upgrade to the next LTS (expected November 2027) in the roadmap |

## Validation spike (2–3 days, gates ADRs 0002 and 0005, not ADR 0001)
Executed 2026-11-26: see [spike results](spike-results.md).

1. **Durable job on PostgreSQL:** kill the worker mid-job 20 times; each job completes once logically; outbox re-dispatch recovers it.
2. **Thumbnail and metadata** in a non-root, no-network container: NetVips vs. SkiaSharp for rendering, MetadataExtractor vs. ExifTool for field coverage. Pass: peak RSS under 512 MiB on a 50 MP JPEG; JPEG/PNG/WebP and EXIF orientation correct; corrupt, oversize and EICAR samples rejected; deterministic output; native library list recorded.
3. **OIDC, cookie and antiforgery** against Keycloak: login/logout, CSRF rejection, 401/403 without leaks.
4. **DocWizz** on the spike code: endpoints, authorization and `DbContext` flows appear in generated docs.

Step 1 validates the in-house Npgsql consumer behind `IJobQueue`. If it fails, evaluate Wolverine before adding a broker per ADR 0002. If neither renderer passes step 2, evaluate Magick.NET (Q8, coder allowlist) and document native licenses and security implications.

## Support evidence
.NET 10 LTS support ends 2028-11-14; Node 26 LTS EOL April 2029, according to current upstream schedules.
- https://dotnet.microsoft.com/en-us/platform/support/policy
- https://nodejs.org/en/blog/announcements/evolving-the-nodejs-release-schedule
- https://www.npgsql.org/efcore/mapping/json.html
- https://www.npgsql.org/efcore/mapping/full-text-search.html
