# Deployment and engineering delivery
Issues #10, #11. Proposed reference deployment.

## Runtime
Docker Engine + Compose v2 on Linux, with Caddy as the only ingress (80/443) and private application/data networks.

- **Services:** web, api, worker, postgres, clamav, caddy, Keycloak candidate, plus a one-off migration container. No queue broker ([ADR 0002](../adr/0002-job-backend.md)). An optional S3-compatible service runs in the test profile.
- **Persistent data:** PostgreSQL (including the job queue), the asset store volume (originals, quarantine, renditions), ClamAV signatures, identity data and Caddy certificates. Document volume ownership and backup coverage.
- **Profiles:** dev/test/prod; no default credentials in production.
- **Images:** pin supported versions/digests after the dependency check; multi-stage builds on minimal .NET and Node base images; non-root where supported.
- **Hardening:** read-only root FS, dropped capabilities, no-new-privileges, resource limits. The parser sandbox has no network; the worker orchestrator keeps the service network.

Observability: Compose healthchecks backed by ASP.NET Core health checks; readiness depends on the database and required services, worker liveness is separate. Structured redacted logs, queue age/failure, API latency/error metrics, disk alerts and OpenTelemetry traces.

Reference host proposal: 4 vCPU, 16 GiB RAM, 1 TB usable asset storage plus separate backup capacity; validate against the #12 workload. Assess amd64/arm64; Podman best-effort until tested; Docker Desktop optional per ADR 0003. Kubernetes is deferred until multi-host HA or sustained scaling demands exceed the Compose operating model.

## Backup/recovery
Proposed RPO 24h, RTO 4h on reference dataset. Quiesce asset writes/workers or establish consistent backup checkpoint; snapshot DB and asset originals, manifest of IDs/checksums, IdP/config/secrets via secure backup, encrypt and store off-host with retention and restore access control. Restore isolated instance, replay/reconcile pending jobs, verify counts, authorization, original hashes and previews; log duration and data-loss window. Rebuild renditions when needed. Preflight upgrades with schema compatibility, verified backup checkpoint, one-off migration container; roll back image only if schema compatible, otherwise forward fix or tested restore with explicit data-loss approval. Run failed migration/recovery drill. No restore or Compose PoC performed.

## Repo layout
- `Zirrus.sln` with `src/Zirrus.Domain`, `src/Zirrus.Application`, `src/Zirrus.Infrastructure`, `src/Zirrus.Api`, `src/Zirrus.Worker`, and `tests/Zirrus.*.Tests`. The names match DocWizz's default C# layer rules.
- `Directory.Build.props` (nullable, analyzers, warnings as errors) and `Directory.Packages.props` (central versions); NuGet lock files; `global.json` pins the SDK.
- `apps/web`: pnpm workspace for the React SPA and the generated API client (`packages/api-client`); `packages/ui`.
- `docs/architecture`, `docs/adr`, `docs/diagrams`, `tests/e2e`, `poc/`.

Short-lived branches; each PR references its issues; conventional commits; CODEOWNERS roles pending actual owners; one approving review before merge. Group related issues in a PR with per-issue acceptance evidence, and keep unresolved items open. No auto-close for partial design.

## CI gates
- **Every PR:**
  - DocWizz documentation and architecture check (#16);
  - `dotnet build` with analyzers, `dotnet test` (unit and integration with a PostgreSQL Testcontainer);
  - pnpm lint, strict TypeScript and Vitest for the web app;
  - OpenAPI contract diff and regeneration check for the TS client;
  - coverage gate #12;
  - NuGet/npm dependency and license scan, SBOM and container scan.
- **Release candidate:** E2E critical paths, accessibility and security tests, with report artifacts.
- **Pipeline security:** least-privilege CI permissions, no secrets in untrusted PR jobs, protected release publishing.
- **Releases:** SemVer, immutable image tags and digests, OCI labels, signed release artifacts where supported.

No application or CI implementation is part of this design PR. The walking skeleton starts after #2 and #11 acceptance per #14.
