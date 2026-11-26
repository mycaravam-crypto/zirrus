---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/2
---

# ADR 0003: Dependency licensing policy

## Context
ZIRRUS is Apache-2.0. Distribution may include container images and native binaries; deployment and commercial service models are not final.

## Options
Permissive-only dependencies; permissive-first with reviewed exceptions; unrestricted dependency selection.

## Proposed decision
Permissive-first with documented exceptions. Default candidates: MIT, BSD-2/3-Clause, Apache-2.0, ISC, PostgreSQL license. Review MPL, LGPL, GPL, AGPL, Artistic and other licenses case by case; source-available/noncommercial, unknown or incompatible terms block distribution until approved or replaced. Separate process/container boundaries do not automatically remove redistribution obligations. Review actual linkage, modifications, notices, source obligations and service terms. Do not distribute unknown-license artifacts.

## Controls
Generate SBOM for pinned runtime and build dependencies, OS packages and container images. Retain notices, license texts and required source/offer materials. Review changes in CI (#11), record exceptions with owner and approval, and obtain qualified legal review for uncertain obligations. See [initial inventory](../architecture/third-party-license-inventory.md).

## Developer tooling
Docker Engine on Linux and Compose are preferred candidates. Docker Desktop is optional and subject to its separate [subscription terms](https://docs.docker.com/subscription-billing/desktop-license/); verify organizational eligibility or obtain approved paid seats before use. Podman is an alternative candidate subject to #10 compatibility validation. Do not make Docker Desktop mandatory.

Budget approval is independent; see [ADR 0004](0004-operating-budget.md). No legal clearance is implied.
