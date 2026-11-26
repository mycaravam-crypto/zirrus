---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/13
---

# ADR 0010: MVP scope and quality baseline

## Context and decision drivers
Deliver user value early while preserving security, integrity and recoverability.

## Considered options
- Image-focused MVP: validates core DAM workflow with manageable media scope.
- Broad Cumulus parity: larger initial value for migrations but substantially more feature and test complexity.
- Minimal upload-only prototype: fastest demo but insufficient catalog/search/security value.

## Proposed decision
Prefer image-focused managed DAM: import, metadata, categories, permission-aware search, previews, retrieval, audit and reliable deployment. Defer NAS, versions, workflows, publishing and AI. Cumulus import needs explicit product decision. Proposed Q1–Q10 remain subject to review.

## Consequences
- Smaller initial format and feature scope; document limitations and extension points.
- Release cannot bypass security, integrity or recovery gates for speed.
- No release owner, date or budget assumed.

## Acceptance evidence
Approve scope and quality thresholds, resolve/defer ADRs under #1 exception rules, record risks, create native #14 implementation subissues with acceptance criteria and dependencies. See [scope](../architecture/quality-mvp-roadmap.md#scope-proposal) and [work packages](../architecture/quality-mvp-roadmap.md#proposed-implementation-work-packages-not-yet-native-subissues).

Validation and approving review pending; no executed PoC implied.
