# MVP quality baseline and delivery plan
Issues #12, #13, #14. All figures proposed, not measured. Confirm product owner acceptance before gate.

## Reference workload
100,000 catalog records, 10,000 representative binary images (remaining records synthetic for query benchmark), median 5 MiB / p95 25 MiB, max 100 MiB, 10 concurrent users, 3 catalogs with mixed permissions; 4 vCPU/16 GiB reference host. Backup capacity separate; size full binary corpus explicitly for recovery drill. Seed deterministic metadata and generated non-sensitive images; include corrupt/oversize and safe EICAR samples.

| ID | Release gate proposal | Method / owner |
|---|---|---|
| Q1 | keyword p95 <= 500ms; filter p95 <= 750ms at 10 concurrent users, <1% unexpected errors | k6 representative queries, #6/#12 |
| Q2 | >= 10 valid 5 MiB JPEG assets/min sustained 15 min; no lost assets | ingestion load harness #7 |
| Q3 | zero unauthorized reads/writes/counts/previews in role matrix | integration + E2E #9 |
| Q4 | 100% original checksum match after restore; RPO <=24h, RTO <=4h | full-size recovery drill #10 |
| Q5 | worker crash/retry produces one logical result per stage, no premature publication | fault injection #7 |
| Q6 | critical workflows WCAG 2.2 AA, zero critical/serious axe findings plus manual keyboard review | Playwright/axe #8 |
| Q7 | zero unresolved release-blocking security findings; severity and exception approval defined by #9 | SCA, Trivy, ZAP, review |
| Q8 | >=80% branch coverage in core domain/application modules; all critical acceptance scenarios pass | xUnit (backend), Vitest (web), integration/E2E #12 |
| Q9 | failed migration recovery preserves authorized asset access and checksums within RTO/RPO | recovery test #10 |
| Q10 | audit mutation denied for application identity; digest mismatch detected | security integration #9 |

Map Q1/Q2 to performance efficiency, Q3/Q7/Q10 security, Q4/Q5/Q9 reliability, Q6 interaction capability, Q8 maintainability (coverage is supporting evidence only). Unit tests domain, integration real DB/queue/storage, API contract OpenAPI, E2E user journeys, performance/security/recovery release suite. Evidence includes commit, environment, dataset seed, result and reviewer. No tests executed; all release evidence pending.

## Scope proposal
Must: authenticated catalog-scoped roles; managed image import/quarantine/scan, metadata, categories, search/list/preview/download, deletion/audit, deployment/restore and CI. Should: bulk selection, retry UX, basic operational dashboard. Could: German translation, saved searches. Won't initially: NAS references, versions, workflow engine, public portal, AI, semantic search, OpenSearch, video, multi-tenant SaaS, Cumulus catalog migration (explicit product decision required; if required, scope and map sample export before approval).

## Proposed implementation work packages (not yet native subissues)
| Work package | Depends on | Acceptance evidence | Rough size |
|---|---|---|---|
| W1 walking skeleton/CI (.NET solution + React app) | #2/#11 approval | clean Compose boot, CI build, DocWizz layer rules pass | M |
| W2 identity/catalog permissions | W1 | Q3 role tests | M |
| W3 data schema/storage adapter | W1/W2 | migration, round-trip, hashes | M |
| W4 ingestion worker/scan/preview | W3 | Q2/Q5 pipeline tests | L |
| W5 metadata/categories/search | W2/W3 | Q1/Q3 queries | L |
| W6 asset explorer/edit/upload UX | W4/W5 | critical E2E Q6 | L |
| W7 audit/retention/recovery | W2–W5 | Q4/Q9/Q10 | L |
| W8 release hardening/docs | W1–W7 | all gates, release evidence | M |

S/M/L relative sizing only; team to estimate. Create native implementation subissues under #14 after #13 approval. Do not imply release date or owner is assigned.

## Consolidated review questions
Backend runtime is decided (ASP.NET Core, ADR 0001). Confirm the PostgreSQL job queue (ADR 0002) and EF Core (ADR 0005) after the validation spike; single organization vs tenancy; managed filesystem vs S3 provider; formats/limits; Keycloak or existing OIDC; retention; initial workload and thresholds; budget/owner/date; whether Cumulus migration required. Review all ADRs, resolve or formally defer with safe default and owner. PoCs: database metadata/search, storage round trip, ingestion, queue compatibility, Compose and backup/restore. No architecture approval claimed. Walking skeleton exception stays unchanged.
