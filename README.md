# ZIRRUS

Web-based digital asset management (DAM). Design and decisions: [`docs/`](docs/architecture/) and [ADRs](docs/adr/README.md). Delivery plan: epic [#14](https://github.com/mycaravam-crypto/zirrus/issues/14).

## Repository layout (ADR 0011)
| Path | Content |
|---|---|
| `Zirrus.slnx`, `src/Zirrus.*` | .NET 10 backend: Domain, Application, Infrastructure, Api, Worker |
| `tests/Zirrus.*.Tests` | xUnit v3 unit and integration tests |
| `apps/web` | React + Vite single-page app |
| `packages/api-client` | OpenAPI document and the TypeScript client generated from it |
| `deploy/` | Dockerfiles, Caddy configuration, Compose files |
| `tools/ci` | CI helpers (license gate) |
| `docs/`, `poc/` | Design documentation; throwaway spikes |

## Prerequisites
- .NET SDK 10.0.1xx (`global.json`), Node.js 26 (`.nvmrc`) with Corepack (`npm install -g corepack@0.36.0`; Node 25+ does not bundle it), Docker Engine with Compose v2.
- PostgreSQL 18 for local runs, either from Compose (`compose.dev.yaml`) or a local server.

## Build and test
```bash
dotnet restore Zirrus.slnx --locked-mode
dotnet build Zirrus.slnx              # also regenerates packages/api-client/openapi/zirrus-api.json
dotnet test --solution Zirrus.slnx    # integration tests start PostgreSQL 18 with Testcontainers

corepack enable                       # after installing Corepack, see Prerequisites
pnpm install --frozen-lockfile
pnpm generate                         # regenerates packages/api-client/src/schema.ts
pnpm typecheck && pnpm lint && pnpm test
```

Without Docker, point the integration tests at an existing PostgreSQL server. They create and drop their own database:
`ZIRRUS_TEST_DB='Host=127.0.0.1;Database=postgres;Username=...;Password=...' dotnet test --solution Zirrus.slnx`

Commit the regenerated OpenAPI document and client with any API change; CI fails when they are stale.

## Run with Docker Compose
```bash
deploy/init-secrets.sh                                    # creates deploy/secrets/ (git-ignored)
docker compose -f deploy/compose.yaml up -d --build --wait
open http://localhost                                     # SPA; /api/v1/catalogs returns JSON
```

Set `ZIRRUS_SITE_ADDRESS` to a domain name for automatic HTTPS. For local development, start only the database with `docker compose -f deploy/compose.yaml -f deploy/compose.dev.yaml up -d postgres migrate`. Then run the API with `ConnectionStrings__Zirrus='Host=localhost;Database=zirrus;Username=zirrus;Password=<deploy/secrets/postgres_password>' dotnet run --project src/Zirrus.Api` and the web app with `pnpm --filter @zirrus/web dev`.

## Sign-in configuration (W2)
- **Identity provider:** set `ZIRRUS_OIDC_AUTHORITY` (e.g. `https://id.example.org/realms/zirrus`) and provide the client secret as the Docker secret or setting `Auth__ClientSecret`. Without an authority, `/login` answers 503.
- **Roles:** the identity provider's realm role `admin` makes a user an organization administrator; map realm roles to a `roles` claim in the ID token. Administrators grant `editor` or `viewer` per catalog with `PUT /api/v1/catalogs/{id}/members/{subject}`.
- **Key ring:** `deploy/init-secrets.sh` also creates `dataprotection_key`, which encrypts the Data Protection key ring stored in PostgreSQL. Production refuses to start without it.
- **Test realm:** `deploy/keycloak/zirrus-realm.json` and `compose.test.yaml` run Keycloak for the CI end-to-end test. Their credentials are test-only.

## Assets and storage (W3)
- **Model:** organizations, catalogs, assets (lifecycle state, SHA-256, revision, JSONB metadata), renditions, categories, metadata schemas and job intents. Composite foreign keys keep every row inside its catalog.
- **Storage:** `Storage__Provider` is `filesystem` (default; `Storage__Root`, the `blobs` volume in Compose) or `s3` (`Storage__S3__ServiceUrl`, `Bucket`, `AccessKey`, `SecretKey`). Back up `blobs` together with `pgdata`.
- **Concurrency:** `GET /api/v1/catalogs/{catalogId}/assets/{assetId}` returns the revision as the ETag. `PUT …/metadata` requires `If-Match` and returns 412 on a stale revision and 428 when it is missing.
- **Reconciliation:** the worker logs a report every 6 hours (`Reconciliation__Interval`, `Reconciliation__StaleAfter`). It never deletes anything.
- **S3 tests:** these use SeaweedFS through Testcontainers. Without Docker, set `ZIRRUS_TEST_S3` to an S3-compatible endpoint.

## Ingestion (W4)
- **Upload:** `POST /api/v1/catalogs/{catalogId}/assets` accepts multipart/form-data with one part named `file`, from editors only, with the CSRF token in `X-CSRF-TOKEN`.
  - JPEG, PNG and WebP are detected from magic bytes; the limit is 100 MiB.
  - The original is streamed into quarantine, and the response lists catalog duplicates by SHA-256.
- **Pipeline:** the worker claims jobs from the PostgreSQL queue (`Ingestion__Concurrency`, `Ingestion__Lease`).
  - **Stages:** `scan` (ClamAV over clamd TCP, fail-closed; `Scanner__Host`, `Scanner__Port`), then `process` (SkiaSharp: a 320 px WebP thumbnail and a 1600 px JPEG preview, EXIF orientation applied, at most 50 megapixels), then active.
  - **Failures:** transient failures retry three times with backoff, then the job is dead-lettered. Editors call `POST …/assets/{assetId}/retry`.
- **Downloads:** `GET …/assets/{assetId}/content` supports one byte range per request, and `GET …/renditions/{thumbnail|preview}` serves the renditions. Both answer for active assets only, with `Cache-Control: private, no-store`.
- **ClamAV:** the `clamav` service needs outbound access for signature updates. Until it is healthy, scans retry.
- **Isolated parser:** the worker sends originals to the `renderer` service (`Renderer__Url`), which has no secrets and no route to the database or the internet. It decodes each file in a fresh child process (`Parser__Timeout`, 60 s; `Parser__MaxMemoryBytes`, 512 MiB). Outside Development the worker refuses to start without it.
- **Load tests:** `tools/ci/ingestion-load.py faults` kills the worker during processing (CI); `soak` checks Q2 throughput (nightly `ingestion-soak` workflow).
- **Tests without Docker:** set `ZIRRUS_TEST_CLAMAV=host:port` to point the real-clamd tests at an existing daemon.

## Asset explorer (W6)
- **Explorer:** the SPA shows the catalogs you hold a role in, a thumbnail grid (newest first, cursor pages of 50 from `GET /api/v1/catalogs/{catalogId}/assets`) and a detail pane with preview, file facts and download.
- **Editors:** upload with progress, retry failed or stalled assets, and edit metadata. Saves send `If-Match`; a 412 shows a conflict and offers to reload. Viewers get read-only views, and the API enforces the same.
- **Status:** the grid and detail poll every 3 s while assets are scanned or processed. A 401 returns to the sign-in link and drops cached data.
- **Language:** strings live in `apps/web/src/i18n.ts`, in English and German, chosen by the browser language.
- **End-to-end tests:** `pnpm --filter @zirrus/web e2e` runs Playwright and axe-core (zero critical or serious findings) against the running Compose stack; CI runs it in the compose job. Search, filters and the category tree follow W5 (#25).

## Adding a database migration
```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Zirrus.Infrastructure -s src/Zirrus.Infrastructure -o Persistence/Migrations
dotnet ef migrations script -p src/Zirrus.Infrastructure -s src/Zirrus.Infrastructure --idempotent   # review the SQL in the PR
```

## CI gates
Every pull request runs:
- **DocWizz:** documentation, concision and layer rules.
- **Backend:** locked restore, build with warnings as errors, unit tests with ≥80% branch coverage on domain and application, integration tests on PostgreSQL 18, OpenAPI document check.
- **Frontend:** client regeneration check, type check, lint, tests, build.
- **Supply chain:** SBOMs, license gate (`tools/ci/license-policy.json`), vulnerability checks.
- **Compose smoke test:** a clean-host boot through Caddy.

## License
Apache-2.0, see [LICENSE](LICENSE).
