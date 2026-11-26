# Initial third-party license inventory
Status: preliminary, not a release SBOM. Verify exact package versions, transitive dependencies, images, binary build flags and bundled libraries before approval.

| Component | Expected upstream license | Review disposition |
|---|---|---|
| .NET SDK/runtime, ASP.NET Core, EF Core | MIT | Notices; verify bundled third-party components |
| Npgsql, Npgsql EF Core provider | PostgreSQL License | Verify selected packages |
| NetVips / libvips | MIT / LGPL-2.1-or-later (verify native package) | Review native linkage and bundled codecs |
| xUnit, Testcontainers for .NET | Apache-2.0; MIT respectively (verify) | Test-only; verify versions |
| Wolverine, Rebus (queue candidates) | MIT (verify exact packages) | Wolverine first spike; Npgsql consumer fallback |
| Mapperly / FluentValidation | Apache-2.0 | Optional; prefer direct mapping and built-in validation |
| Shouldly / NSubstitute | BSD-3-Clause (verify) | Optional test-only |
| Magick.NET / SkiaSharp | Apache-2.0 / MIT (wrapper; verify native bundles) | Rendering candidates; compare with NetVips |
| MetadataExtractor | Apache-2.0 (verify) | Metadata candidate; compare with ExifTool |
| Apache JMeter | Apache-2.0 | Preferred load-test tool; verify plugins |
| ImageSharp / NBomber v5+ | conditional split license / commercial terms (verify) | Avoid by default |
| Hangfire, MassTransit v9+, MediatR, AutoMapper, FluentAssertions v8+ | LGPL-3.0 or commercial (verify current terms) | Avoid; see ADR 0003 review list |
| TypeScript, Node.js (frontend build only) | Apache-2.0; MIT respectively | Notices for shipped bundles only |
| React, Vite, Tailwind, shadcn/ui, TanStack | MIT (candidate packages) | Verify copied components and transitive dependencies |
| OpenAPI TS client generator (e.g. openapi-typescript, Orval) | MIT (verify selected tool) | Build-time only |
| PostgreSQL | PostgreSQL | Verify extensions |
| Valkey / Redis | BSD-3-Clause / RSALv2, SSPLv1, AGPLv3 | Not used in the MVP (ADR 0002); review again only if a broker is added |
| ExifTool | Same terms as Perl (Artistic or GPL option; verify exact terms) | Review distribution notices |
| ClamAV | GPL-2.0 family (verify release) | Review separate service distribution/source obligations and signatures |
| FFmpeg | LGPL-2.1-or-later default; GPL with optional parts | Deferred; inspect build flags, codecs and redistribution terms |
| Keycloak / Caddy | Apache-2.0 | Verify plugins and images |
| Docker Engine (standalone) / Docker Compose | Apache-2.0 | Verify distribution and base images separately |
| Docker Desktop | Docker Subscription Service Agreement | Optional; free eligibility or paid subscription must be verified; government entities require paid subscription |
| Podman | Apache-2.0 (candidate; verify release) | Alternative runtime; validate Compose compatibility in #10 |
| pnpm | MIT | Verify selected version |
| OpenTelemetry (.NET, JS) | Apache-2.0 | Verify exporters |
| OpenSearch (deferred) | Apache-2.0 | Verify plugins |
| Vitest, Testing Library, Playwright, k6, axe-core, ZAP, Trivy, CycloneDX | Mixed; exact packages TBD | Inventory build/test tooling before adoption; prefer JMeter; k6 only by reviewed test-tool exception |
| DocWizz | Apache-2.0 | CI-only tool, not distributed |
| GitHub Actions / Dependabot | Service terms and individual action licenses | Pin and review actions |

Primary references: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT ; https://github.com/npgsql/npgsql/blob/main/LICENSE ; https://www.libvips.org/ ; https://exiftool.org/ ; https://www.clamav.net/ ; https://www.ffmpeg.org/legal.html ; https://redis.io/legal/licenses/ . Docker references: https://docs.docker.com/engine/ and https://docs.docker.com/subscription-billing/desktop-license/ . Entries marked "verify" are preliminary and must be confirmed against versioned upstream license files. No legal approval claimed. See [open-source dependency selection](open-source-dependencies.md) for alternatives and admission evidence.
