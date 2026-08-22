<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Mandate for a complete product overhaul in v6.0.0, superseding a real v5.0.0 release|
|Version|02|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-22)|
|Changed|Accepted (2026-08-22)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Mandate for a complete product overhaul in v6.0.0, superseding a real v5.0.0 release

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision on 2026-08-22, following a design analysis conducted before any v6.0.0 implementation work started.

Technical Story: [ADR006V01](#) authorized "no commitment to the current version" **for v5.0.0 specifically**, promising strict SemVer to resume from v5.0.0 onward, and explicitly rejected treating every future major as a standing license for free breaking changes. Since then, **v5.0.0 was actually released**: verified directly against the NuGet Gallery during this analysis, `RingBufferPlus` v5.0.0 is published, marked "latest", with 90 downloads, and versions 4.0.1 and earlier are now marked deprecated — the same pattern this project applies to every superseded major. This version supersedes V01's account of what "resuming SemVer from v5.0.0" means now that v5.0.0 is a real, shipped artifact, and extends the mandate to v6.0.0 on its own terms rather than by silent analogy.

## Context and Problem Statement

A further design analysis (concurrency ownership model, autoscale algorithm, and remaining public surface — see `doc/architecture/v6-design-proposal.md`) produced real evidence for another round of breaking changes: a decision-quality simulation showing the shipped median autoscaling algorithm can get structurally stuck over-provisioned, and a real, run benchmark (`AutoScaleReactionBenchmarks`) showing the current reactive path is dominated by configured timeout, not actual scaling cost. Both findings require public surface and behavioral changes that are not minor-version-compatible. The question ADR006V01 already anticipated now applies for real: is authorizing this "every future major gets a free pass" — the option V01 already rejected — or does it stand on its own?

## Decision Drivers

* v5.0.0 is a real, published release with real (if few) consumers — unlike the state assumed when this analysis began, there is now an actual SemVer promise in effect to reason about, not a hypothetical one.
* The evidence behind v6.0.0's changes (real benchmarks, a real algorithm-comparison simulation) is substantive, not "we can break things anyway, might as well" — the same evidentiary standard ADR003 already holds itself to.
* A second breaking major within roughly ten days of the first is an unusual pattern that needs to be named and explained in the release communication, not minimized or hidden.
* Without an explicit restatement of "SemVer resumes afterward", accepting this mandate for v6.0.0 risks exactly the "every major is a free pass" precedent ADR006V01 rejected.

## Considered Options

* Ship the v6.0.0 changes as v5.1.0, since ADR006V01 promised SemVer discipline from v5.0.0 onward.
* Authorize v6.0.0 as its own coordinated, deliberate overhaul, on the same "no commitment to the current version" terms as v5.0.0, with its own stated justification — and restate that strict SemVer resumes from v6.0.0, not v5.0.0.
* Treat the v5.0.0 mandate as a standing precedent that automatically extends to v6.0.0 without a fresh decision.

## Decision Outcome

Chosen option: "Authorize v6.0.0 as its own coordinated overhaul, with its own justification, restating that SemVer resumes from v6.0.0 onward" — because the alternative of labeling this v5.1.0 would mean shipping publicly-breaking changes (new required capacity concept, redesigned `SwitchToAsync` semantics, an autoscale algorithm swap, a rewritten public surface) under a minor-version number, silently violating the exact SemVer promise ADR006V01 made rather than transparently invoking a new exception — a worse outcome for consumer trust than an honest major bump. The third option (silent automatic extension) is explicitly rejected: it is indistinguishable from the "every future major is a free pass" pattern ADR006V01 already rejected, just applied one release later instead of never.

This ADR authorizes, for v6.0.0, each item below (details and trade-offs live in each item's own ADR — this list does not repeat them, only references them):

* **Concurrency ownership model** — Orquestrador (single owner of capacity truth) / Fábrica / Remoção / Monitor, see [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md).
* **Autoscale algorithm** — percentile + linear-regression trend, replacing the median, backed by a real comparison simulation, see [ADR003V03](./ADR003V03-median-sample-autoscaling-algorithm.md).
* **Public fluent surface** — explicit `target` capacity, `SwitchToAsync` redesigned as a temporary pin, `HeartBeat`/`Logger`/DI surface cleanup, see [ADR007V03](./ADR007V03-redesign-of-the-public-fluent-api-surface.md).

**Superseding ADR006V01's SemVer-resumption clause**: ADR006V01 stated strict SemVer resumes "from v5.0.0 onward". That promise's target release has moved: strict SemVer now resumes **from v6.0.0 onward**, once it ships. This is not a reopening of the rejected "every major is free" option — it is a one-time correction because the release that was supposed to carry that promise forward is itself being superseded, on its own justified terms, before any further major beyond it.

### Positive Consequences

* Names and justifies the fast v5.0.0→v6.0.0 turnover explicitly, instead of letting it read as an unexplained pattern of "every major breaks everything".
* Grounds the decision in real evidence generated during this analysis (benchmarks, a real algorithm-comparison simulation) rather than "compatibility no longer matters, so why not".
* Preserves the actual protection ADR006V01 cared about: this is not a standing precedent — a v7 wanting the same freedom needs its own decision, not a citation of this one.

### Negative Consequences

* Consumers who adopted v5.0.0 (however few) face a second breaking migration in quick succession — this must be stated plainly in the v6.0.0 CHANGELOG, following the project's own established deprecation-on-supersede pattern (4.0.1, 4.0.0, 3.2.0, 3.1.0 were all marked deprecated immediately on being superseded).
* Concentrates two migration cycles close together instead of the "single coordinated window" ADR006V01 originally intended — an explicit, acknowledged cost of the evidence for v6.0.0 not existing yet when v5.0.0 shipped.

## Pros and Cons of the Options

### Ship as v5.1.0 (rejected)

* Good, because it avoids naming a second major version bump so soon after the first.
* Bad, because it would silently violate the SemVer discipline ADR006V01 promised to resume from v5.0.0 — worse than an honest major bump.

### v6.0.0 as its own justified overhaul (chosen)

* Good, because it is SemVer-honest given the actual scope of change.
* Good, because it restates (rather than silently drops) the "discipline resumes afterward" commitment ADR006V01 cared about.
* Bad, because it requires explicit, uncomfortable communication about a fast major-to-major turnover.

### Silent automatic extension of the v5.0.0 mandate (rejected)

* Good, because it requires no new decision or justification.
* Bad, because it is exactly the "every future major is a free pass" pattern ADR006V01 already rejected for destroying SemVer trust.

## Links

* Refines: [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) — concurrency ownership model authorized for v6.0.0.
* Refines: [ADR003V03](./ADR003V03-median-sample-autoscaling-algorithm.md) — autoscale algorithm swap authorized for v6.0.0, backed by evidence.
* Refines: [ADR007V03](./ADR007V03-redesign-of-the-public-fluent-api-surface.md) — remaining public surface changes authorized for v6.0.0.
* Supersedes (partially): [ADR006V01](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — the SemVer-resumption target moves from v5.0.0 to v6.0.0; v5.0.0's own mandate and scope decisions remain historically valid.
* Supersedes (partially): [ADR004V02](./ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md) — see [ADR004V03](./ADR004V03-semantic-versioning-policy-and-fluent-api-stability.md), which this ADR authorizes: Stage (b)'s SemVer-resumption point moves from v5.0.0 to v6.0.0; everything else in ADR004's regime (the Stage (a)/(b) split, the v4.x total-cutoff support policy) remains unchanged.
