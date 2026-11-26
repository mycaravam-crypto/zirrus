# ADR 0003: Dependency licensing and operating budget
Status: Proposed
Date: 2026-11-26
Issue: #2

## Context
ZIRRUS is Apache-2.0. Distribution may include container images and native binaries; deployment and commercial service models are not final.

## Options
Permissive-only dependencies; permissive-first with reviewed exceptions; unrestricted dependency selection.

## Proposed decision
Permissive-first with documented exceptions. Default candidates: MIT, BSD-2/3-Clause, Apache-2.0, ISC, PostgreSQL license. Review MPL, LGPL, GPL, AGPL, Artistic and other licenses case by case; source-available/noncommercial, unknown or incompatible terms block distribution until approved or replaced. Separate process/container boundaries do not automatically remove redistribution obligations. Review actual linkage, modifications, notices, source obligations and service terms. Do not distribute unknown-license artifacts.

## Controls
Generate SBOM for pinned runtime and build dependencies, OS packages and container images. Retain notices, license texts and required source/offer materials. Review changes in CI (#11), record exceptions with owner and approval, and obtain qualified legal review for uncertain obligations. See [initial inventory](../architecture/third-party-license-inventory.md).

## Budget
Prefer open-source self-hostable components without mandatory paid tiers. Compare infrastructure and operational labor using the cost model in the stack evaluation; budget owner, ceiling and distribution model require confirmation. No legal clearance or cost approval is implied.
