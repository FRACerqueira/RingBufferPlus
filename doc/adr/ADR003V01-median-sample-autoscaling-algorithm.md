<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Median-sample autoscaling algorithm|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Median-sample autoscaling algorithm

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: `RingBufferManager<T>.CreateTaskbufferAutoScale` collects `SamplesCount` buffer-availability samples over a `SamplesBase` window, computes the median, and decides to scale down when the value crosses thresholds derived from `MinCapacity`/`Capacity`/`MaxCapacity` (formulas documented in `IRingBufferScaleCapacity.AutoScaleAcquireFault`).

## Context and Problem Statement

The current algorithm smooths transient noise/spikes using a median of samples, but this delays reaction to real load changes, and there is no benchmark or quantitative data (the product's central value proposition is "resource-conscious usage") measuring actual reaction time, nor unit tests isolating the statistical calculation from the surrounding async side effects. Should we swap the algorithm, make it pluggable, or simply instrument it and leave it as is?

## Decision Drivers

* No evidence that the current algorithm is problematic in production — swapping it without data is risky.
* Swapping the algorithm is an observable behavior change for consumers (a breaking behavioral change), even without breaking the public signature — see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md).
* Lack of deterministic testability of the calculation today (mixed with async loops and side effects in `CreateTaskbufferAutoScale`).
* Lack of a benchmark quantifying the smoothing-vs-reaction-latency trade-off.

## Considered Options

* Keep the median-of-samples-over-a-fixed-window as is.
* Replace it with a deterministic rule ("scale down if availability > X for Y consecutive ms") — simpler and more predictable, but more sensitive to noise/flapping.
* Make the scale-decision algorithm pluggable (strategy pattern), letting the consumer choose median, sliding window, EWMA, etc.

## Decision Outcome

Chosen option: "Keep the median of samples as is", because there is no data to justify the swap, and any observable behavior change requires prior measurement. Required follow-up action (see Action Plan, Phase 4): (1) extract the median calculation and threshold decision into an isolated, testable pure method, with unit tests using fixed sample arrays and edge cases (even/odd counts, all equal, etc.); (2) create a benchmark project (BenchmarkDotNet) measuring reaction time to synthetic load changes. The "pluggable" option remains registered as a candidate for future revision of this ADR (via `adrplus revise`) if the benchmark shows a real need for alternative strategies.

**Revised on 2026-08-11 under the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate:** the maintainer removed any commitment to the current version for v5.0.0 — not just "breaking changes allowed where necessary", but a total absence of any obligation to preserve v4.x. This *still* does not change this ADR's conclusion, and the reason is specific: choosing an autoscaling algorithm is a **question of evidence**, not a **question of compatibility**. Removing the compatibility constraint does not create the missing data needed to justify the swap — the two things are orthogonal. Swapping the algorithm now, just because "since it can break anyway, might as well", would be deciding by inertia of the breaking-change window, not by technical merit — exactly the kind of scope creep [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) explicitly asks to avoid. The decision stands: keep the median, extract it into a testable function, measure with a benchmark before considering any alternative. ADR006 lists this exclusion with that explicit criterion (evidence, not compatibility).

### Positive Consequences

* No behavior change for existing consumers.
* Opens the path to future decisions based on data (benchmark), not intuition.

### Negative Consequences

* The reaction delay inherent to the median remains unsolved until data justifies further investment.
* Until the calculation is extracted into a pure function, the scale-down logic remains hard to test in isolation.

## Pros and Cons of the Options

### Keep the median as is (chosen)

* Good, because it carries zero risk of behavior regression for current consumers.
* Good, because it requires no rewrite effort before having data.
* Bad, because it does not by itself solve the lack of testability/benchmark (requires the follow-up action).

### Deterministic threshold/time rule

* Good, because it is simpler to explain and predict.
* Bad, because it is more prone to flapping (repeated scale up/down) under oscillating load — with no data proving it would be better.

### Pluggable strategy

* Good, because it gives maximum flexibility to advanced consumers.
* Bad, because it increases the public API surface (one more extension point to maintain and document) without yet-proven demand.

## Links

* Related: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — observable behavior changes to scaling also count as a relevant change for consumers, even without altering signatures.
