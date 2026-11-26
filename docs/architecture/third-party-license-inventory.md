# Initial third-party license inventory
Status: preliminary, not a release SBOM. Verify exact package versions, transitive dependencies, images, binary build flags and bundled libraries before approval.

| Component | Expected upstream license | Review disposition |
|---|---|---|
| Node.js, TypeScript | MIT; Apache-2.0 respectively | Notices; bundled components |
| React, Vite, Tailwind, shadcn/ui, TanStack | MIT (candidate packages) | Verify copied components and transitive dependencies |
| NestJS, Drizzle ORM | MIT; Apache-2.0 respectively | Verify selected packages |
| PostgreSQL | PostgreSQL | Verify extensions |
| BullMQ OSS | MIT | Exclude commercial Pro features unless approved |
| Valkey | BSD-3-Clause | Preferred |
| Redis 7.4 / 8 | RSALv2 or SSPLv1 / additionally AGPLv3 | Exception review |
| Sharp / libvips | Apache-2.0 / LGPL-2.1-or-later (verify binary) | Review native linkage and bundled codecs |
| ExifTool | Same terms as Perl (Artistic or GPL option; verify exact terms) | Review distribution notices |
| ClamAV | GPL-2.0 family (verify release) | Review separate service distribution/source obligations and signatures |
| FFmpeg | LGPL-2.1-or-later default; GPL with optional parts | Deferred; inspect build flags, codecs and redistribution terms |
| Keycloak / Caddy | Apache-2.0 | Verify plugins and images |
| Docker Engine (standalone) / Docker Compose | Apache-2.0 | Verify distribution and base images separately |
| Docker Desktop | Docker Subscription Service Agreement | Optional; free eligibility or paid subscription must be verified; government entities require paid subscription |
| Podman | Apache-2.0 (candidate; verify release) | Alternative runtime; validate Compose compatibility in #10 |
| pnpm | MIT | Verify selected version |
| OpenTelemetry | Apache-2.0 | Verify exporters |
| OpenSearch (deferred) | Apache-2.0 | Verify plugins |
| Vitest, Testing Library, Supertest, Testcontainers, Playwright, k6, axe-core, ZAP, Trivy, CycloneDX | Mixed; exact packages TBD | Inventory build/test tooling before adoption; assess k6 AGPL obligations |
| GitHub Actions / Dependabot | Service terms and individual action licenses | Pin and review actions |

Primary references: https://redis.io/legal/licenses/ ; https://valkey.io/docs/topics/introduction/ ; https://www.ffmpeg.org/legal.html ; https://exiftool.org/ ; https://sharp.pixelplumbing.com/ ; https://www.libvips.org/ ; https://www.clamav.net/ . Docker references: https://docs.docker.com/engine/ and https://docs.docker.com/subscription-billing/desktop-license/ . Docker Engine and Desktop terms were checked during review; remaining unverified entries are preliminary and must be confirmed against versioned upstream license files. No legal approval claimed.
