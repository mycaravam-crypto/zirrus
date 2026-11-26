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
Permissive-first with documented exceptions. Default candidates: MIT, BSD-2/3-Clause, Apache-2.0, ISC, PostgreSQL license.

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
| FluentAssertions v8+ | commercial license | xUnit assertions or Shouldly |

## Controls
- Generate an SBOM for pinned runtime and build dependencies (NuGet, npm), OS packages and container images.
- Use NuGet lock files and central package versions; review license changes on every dependency update in CI (#11).
- Retain notices, license texts and required source/offer materials.
- Record exceptions with owner and approval; obtain qualified legal review for uncertain obligations.

See the [initial inventory](../architecture/third-party-license-inventory.md).

## Developer tooling
Docker Engine on Linux and Compose are preferred candidates. Docker Desktop is optional and subject to its separate [subscription terms](https://docs.docker.com/subscription-billing/desktop-license/); verify organizational eligibility or obtain approved paid seats before use. Podman is an alternative candidate subject to #10 compatibility validation. Do not make Docker Desktop mandatory. The .NET SDK and runtime are MIT-licensed; IDE choice is up to each developer, and no paid IDE is required.

Budget approval is independent; see [ADR 0004](0004-operating-budget.md). No legal clearance is implied.
