---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/11
---

# ADR 0011: Repository structure, CI and supply-chain controls

## Context and decision drivers
The backend is C#/.NET 10 and the frontend React/TypeScript (ADR 0001), so the repository holds two toolchains. The walking skeleton (#21) needs an accepted layout and CI gate set before it starts (#14). Drivers:
- reproducible builds and a single place to review changes;
- DocWizz layer rules that work without custom configuration;
- the licensing controls of ADR 0003;
- lessons from the [validation spike](../architecture/spike-results.md).

## Considered options
- **One repository with a .NET solution and a pnpm workspace:** a single PR can change API, generated client and UI together; one CI pipeline; two toolchains in one place.
- **Separate backend and frontend repositories:** independent toolchains and pipelines; contract changes need coordinated PRs and version pinning of the generated client.
- **Nx/Turborepo-style polyglot monorepo tooling:** task caching across languages; extra tooling with limited .NET support.

## Decision outcome
**One repository** with a .NET solution and a pnpm workspace, using GitHub Actions.

### Layout
| Path | Content |
|---|---|
| `Zirrus.slnx` | .NET solution (SDK 10 default format) |
| `src/Zirrus.Domain`, `src/Zirrus.Application`, `src/Zirrus.Infrastructure`, `src/Zirrus.Api`, `src/Zirrus.Worker` | Layer projects; names match DocWizz's default C# layer rules |
| `tests/Zirrus.<Project>.Tests` | xUnit unit and integration tests |
| `apps/web` | React/Vite SPA |
| `packages/api-client`, `packages/ui` | Generated TypeScript API client; shared UI components |
| `tests/e2e` | Playwright end-to-end and accessibility tests |
| `deploy/` | Compose files, Caddy configuration |
| `docs/`, `poc/` | Design documentation; throwaway spikes (excluded from DocWizz) |

### Build reproducibility
- `global.json` pins the .NET SDK feature band. `Directory.Build.props` enables nullable, recommended analyzers, warnings as errors and documentation files.
- `Directory.Packages.props` sets central package versions; NuGet lock files are committed and restored in locked mode.
- Microsoft.*, EF Core and Npgsql packages stay on the **10.x** line while the runtime is .NET 10. The spike found the 11.0 packages target only `net11.0`.
- `.editorconfig` marks `**/Migrations/*.cs` as generated code.
- The pnpm lockfile is frozen; `packageManager` and `.nvmrc` pin pnpm and Node (frontend build only).
- Actions are pinned by commit SHA.

### Branching and review
- Trunk-based: short-lived branches from `main`; squash merge; conventional commit titles.
- Each PR references its issues and needs one approving review and green CI. CODEOWNERS is added once owners are named.
- Branch protection on `main`: required checks, no force pushes.

### CI gates on every pull request
| Job | Checks |
|---|---|
| docs | DocWizz `check --since` (documentation, concision, architecture layers) |
| backend | `dotnet restore --locked-mode`, `dotnet build` (warnings as errors), `dotnet test` with PostgreSQL 18 via Testcontainers, coverage per #12 |
| frontend | pnpm install (frozen), lint, strict type check, Vitest |
| contract | OpenAPI document generated from the API; diff against `main`; regenerated client must match the committed one |
| supply chain | CycloneDX SBOM for NuGet, npm and native package contents; license check against ADR 0003; vulnerable dependency check |

On release candidates, also run: Playwright E2E with axe-core, ZAP baseline, image build and Trivy scan, and the Q1–Q10 suites from #12.

### Dependency updates
- Dependabot for NuGet, npm, GitHub Actions and Docker base images, grouped weekly.
- Major versions of Microsoft.*, EF Core and Npgsql are ignored until a runtime upgrade ADR. That happens before .NET 10 support ends in November 2028.
- Every update runs the license check; a license change fails the PR.

### Releases
- SemVer tags on `main`; container images published to GHCR with immutable digests and OCI labels.
- SBOM attached to each release. Provenance attestations and image signing where GitHub supports them.
- Release workflow runs only on protected tags, with least-privilege tokens and no secrets in pull-request jobs.

## Consequences
- **Good:** one PR covers contract, client and UI changes. One gate set applies to everything, and DocWizz layer rules work by convention.
- **Bad:** two toolchains in CI and on developer machines. CI time is higher than for either repository alone, so use path filters and caching.
- **Neutral:** splitting the repository later stays possible because the generated client is the only coupling.

## Acceptance evidence
- #21 implements the layout and CI jobs.
- A PR with a deliberate layer violation, a warning, a lockfile drift, a stale generated client and an unapproved license each fails the matching job.
- Clean-host `docker compose up` works from the committed layout.

Validation and approving review pending.
