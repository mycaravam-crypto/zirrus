---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/2
---

# ADR 0003: Dependency licensing policy

## Context
ZIRRUS is Apache-2.0. Distribution may include container images and native binaries; deployment and commercial service models are not final. The backend is .NET (ADR 0001), so NuGet packages join npm packages and OS images in scope.

## Options
Permissive-only dependencies; permissive-first with reviewed exceptions; unrestricted dependency selection.

## Proposed decision
First-party APIs first; permissive, maintained open source second; other terms only by documented exception. Default candidates: MIT, BSD-2/3-Clause, Apache-2.0, ISC, PostgreSQL license. Core operation must not require a paid license, registration key, revenue eligibility or vendor service. See [candidate evaluation](../architecture/open-source-dependencies.md).

- Review MPL, LGPL, GPL, AGPL, Artistic and other licenses case by case.
- Source-available, noncommercial, unknown or incompatible terms block distribution until approved or replaced.
- Separate process/container boundaries do not automatically remove redistribution obligations. Review actual linkage, modifications, notices, source obligations and service terms.
- Do not distribute unknown-license artifacts.

## .NET packages requiring review
Several widely used .NET libraries changed to commercial or copyleft terms. Verify current terms before use; prefer the alternatives.

| Package | Concern | Preferred alternative |
|---|---|---|
| Hangfire | core LGPL-3.0, paid Pro features | PostgreSQL queue per ADR 0002 |
| MassTransit v9+ | commercial license | Wolverine, Rebus or in-house outbox consumer |
| MediatR, AutoMapper (recent versions) | commercial license | plain application services; Mapperly or hand-written mapping |
| FluentAssertions v8+ | noncommercial/community or commercial terms | xUnit assertions or Shouldly |
| ImageSharp (current split license) | conditional terms | NetVips or SkiaSharp; Magick.NET for extended formats |
| NBomber v5+ | commercial terms for organizational use | k6 (test-tool exception) or Gatling OSS |
| XmpCore 6.1.10.1 (MetadataExtractor dependency) | no SPDX expression; Adobe XMP library terms by URL | review the terms before distribution; until then the metadata extractor is not approved ([spike results](../architecture/spike-results.md#3-metadata-7)) |

Check exact versions and license expressions, including dual-license choices. Do not pin obsolete versions simply to avoid a license change.

## Test-tool exceptions
Copyleft tools used unmodified, only in CI or test environments, and never distributed with ZIRRUS may be approved as recorded test-tool exceptions (scope, version, reviewer). Current candidates: k6 for load tests and MinIO as an S3-compatible test server (SeaweedFS preferred). A tool that moves into a shipped image or production deployment loses the exception. Details: [open-source evaluation](../architecture/open-source-dependencies.md#test-tool-exceptions).

## Controls
- Generate an SBOM for pinned runtime and build dependencies (NuGet, npm), OS packages and container images.
- Use NuGet lock files and central package versions; review license changes on every dependency update in CI (#11).
- Native NuGet packages must list their bundled native libraries in the SBOM; CI fails when a native package lacks that list.
- Retain notices, license texts and required source/offer materials.
- Record exceptions with owner and approval; obtain qualified legal review for uncertain obligations.

See the [initial inventory](../architecture/third-party-license-inventory.md).

## Developer tooling
Docker Engine on Linux and Compose are preferred candidates. Docker Desktop is optional and subject to its separate [subscription terms](https://docs.docker.com/subscription-billing/desktop-license/); verify organizational eligibility or obtain approved paid seats before use. Podman is an alternative candidate subject to #10 compatibility validation. Do not make Docker Desktop mandatory. The .NET SDK and runtime are MIT-licensed; IDE choice is up to each developer, and no paid IDE is required.

Budget approval is independent; see [ADR 0004](0004-operating-budget.md). No legal clearance is implied.
