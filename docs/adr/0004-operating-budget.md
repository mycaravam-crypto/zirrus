---
status: proposed
date: 2026-11-26
issue: https://github.com/mycaravam-crypto/zirrus/issues/2
---

# ADR 0004: Operating budget and cost model

## Context and decision drivers
The MVP should be economical to develop and operate. No budget ceiling, reference workload or operator rate is approved.

## Options
- Self-hosted reference deployment: infrastructure and operator effort borne by the operator.
- Managed cloud services: potentially lower maintenance effort, recurring service charges and provider dependencies.
- Hybrid deployment: evaluate only if required by the product scope.

## Proposed decision
Use a self-hosted reference deployment for cost comparison, without preempting the deployment decision in [#10](https://github.com/mycaravam-crypto/zirrus/issues/10). Prefer components without mandatory paid tiers. Evaluate total operating cost using the [cost model](../architecture/technology-stack.md), including developer tooling subscriptions, storage growth, backups, CI, labor and recovery overhead.

## Acceptance evidence
The budget owner confirms the ceiling and assumptions; #10 and #12 supply reference hardware, workload and resource evidence. Record cost estimates, uncertainties and review triggers. Approval is separate from [ADR 0003](0003-dependency-policy.md). No budget is committed by this proposal.
