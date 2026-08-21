<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Median-sample autoscaling algorithm|
|Version|02|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-21)|
|Changed|Accepted (2026-08-21)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Median-sample autoscaling algorithm

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: `RingBufferManager<T>.CreateTaskbufferAutoScale` collects `SamplesCount` buffer-availability samples over a `SamplesBase` window, computes the median, and decides to scale down when the value crosses thresholds derived from `MinCapacity`/`Capacity`/`MaxCapacity` (formulas documented in `IRingBufferElasticBuilder<T>.AutoScaleAcquireFault`).

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

Chosen option: "Keep the median of samples as is", because there is no data to justify the swap, and any observable behavior change requires prior measurement. Required follow-up action: (1) extract the median calculation and threshold decision into an isolated, testable pure method, with unit tests using fixed sample arrays and edge cases (even/odd counts, all equal, etc.); (2) create a benchmark project (BenchmarkDotNet) measuring reaction time to synthetic load changes. The "pluggable" option remains registered as a candidate for future revision of this ADR (via `adrplus revise`) if the benchmark shows a real need for alternative strategies.

**Revised on 2026-08-11 under the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate:** the maintainer removed any commitment to the current version for v5.0.0 — not just "breaking changes allowed where necessary", but a total absence of any obligation to preserve v4.x. This *still* does not change this ADR's conclusion, and the reason is specific: choosing an autoscaling algorithm is a **question of evidence**, not a **question of compatibility**. Removing the compatibility constraint does not create the missing data needed to justify the swap — the two things are orthogonal. Swapping the algorithm now, just because "since it can break anyway, might as well", would be deciding by inertia of the breaking-change window, not by technical merit — exactly the kind of scope creep [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) explicitly asks to avoid. The decision stands: keep the median, extract it into a testable function, measure with a benchmark before considering any alternative. ADR006 lists this exclusion with that explicit criterion (evidence, not compatibility).

**Amended on 2026-08-21 (v5.1.0 hardening, Rodada 3) — Technical Story:** the v5.1.0 viability audit's Round 3 (`TODO/relatorio-viabilidade-ringbufferplus-v5.md`, finding R16) found that `AutoScaleDecision.EvaluateScaleDown` only ever evaluated a scale-down at the *exact* initial or maximum capacity (`isInitCapacity`/`isMaxCapacity`, strict equality). A partial scale-up (a factory tolerating transient failures via `maxConsecutiveFactoryFailures`, see the R14 amendment history in `CHANGELOG.md`) or a partial scale-down (idle items insufficient to reach the full target) can leave the buffer strictly between two named capacities, from which neither equality ever holds again — the buffer got stuck there permanently regardless of how idle it became. This is not a change to the median-of-samples statistic itself (this ADR's actual decision, still unchanged): it is a fix to when and against what threshold the already-decided statistic's result is compared.

**Amendment:** `EvaluateScaleDown` now evaluates by band, not exact equality — above initial capacity (including exactly maximum capacity) is eligible to scale down towards initial capacity; at or below initial capacity, down to minimum capacity, is eligible to scale down towards minimum capacity. The safety margin added to each threshold (previously a fixed value computed once from `MinCapacity`/`Capacity`/`MaxCapacity`, e.g. `maxCapacity - capacity + 2`) is now scaled to the buffer's actual current capacity (e.g. `currentCapacity - capacity + 2`) instead of a fixed tier, so it *generally* reduces to the exact original formula whenever the buffer is exactly at the initial or maximum capacity, but remains evaluable — not mathematically unreachable — from any off-tier position in between. (This "generally" is no longer unqualified after the Rodada 4 amendment below, which caps the same margin — the reduction to the original formula only holds when that cap does not engage, i.e. `capacity`/`minCapacity != 2`.) See `Commands/IRingBufferElasticBuilder.cs` (`AutoScaleAcquireFault`'s XML doc, corrected in the same round) for the exact current formulas.

**Known residual gap at the time of the Rodada 3 amendment above (now fixed - see the Rodada 4 amendment below):** when initial capacity is 2 (the minimum legal value), the upper-band margin `currentCapacity - capacity + 2` equals `currentCapacity` itself — a quantity the median (bounded by `currentCapacity`, since it can never exceed the number of items that exist) can never exceed. Scale-down from above initial capacity was therefore unreachable regardless of position, including exactly at maximum capacity — a pre-existing degenerate case (the original fixed formula had the identical problem: `maxCapacity - capacity + 2` reduces to `maxCapacity` when `capacity = 2`), not introduced by the Rodada 3 amendment.

**Amended on 2026-08-21 (v5.1.0 hardening, Rodada 4) — Technical Story:** Round 4's Resiliência pass reproduced the Rodada-3-registered gap above (**R18**) via an exhaustive deterministic sweep of `EvaluateScaleDown`, confirming `capacity == 2` is the *only* degenerate value for the upper band, and found its exact lower-band mirror (**R19**): when minimum capacity is 2, the lower-band margin `currentCapacity - minCapacity + 2` also reduces to `currentCapacity` itself, but because that band's comparison is `>=` rather than the upper band's strict `>`, the condition is not literally impossible — only reachable when the median equals `currentCapacity` exactly, i.e. zero acquisitions across the entire sampling window, a materially stricter bar than any other minimum capacity value tolerates. Both share one root cause: expressed in terms of active (non-idle) usage rather than idle count, the margin is really an active-usage threshold of `capacity - 2` (upper band) or `minCapacity - 2` (lower band) — which is zero, or negative, whenever the paired boundary constant is 2.

**Amendment:** both thresholds are now capped at `currentCapacity - 1`, guaranteeing the median (bounded by `currentCapacity`) can always in principle cross them. The cap only changes behavior for the `capacity == 2` / `minCapacity == 2` configurations the collapse affected — every other configuration's uncapped threshold was already below this cap and is unaffected. R18 and R19 are both closed by this single, symmetric change; see `Core/AutoScaleDecision.cs` and `Commands/IRingBufferElasticBuilder.cs` (`AutoScaleAcquireFault`'s XML doc, corrected in the same round) for the exact current formulas.

### Positive Consequences

* No behavior change for existing consumers.
* Opens the path to future decisions based on data (benchmark), not intuition.
* *(2026-08-21 Rodada 3 amendment)* A buffer that partially scales up or down (R14/R6) no longer gets stuck off-tier forever — scale-down evaluation continues from wherever it actually lands, with the same margin logic that already applied at the exact tiers.
* *(2026-08-21 Rodada 3 amendment)* `EvaluateScaleDown`'s signature dropped two build-time-precomputed `int?` parameters (`ScaleDownInit`/`ScaleDownMax`) in favor of computing the margin from the same three capacities the decision already needed — fewer stored fields, same inputs.
* *(2026-08-21 Rodada 4 amendment)* Neither `initialCapacity == 2` nor `minCapacity == 2` (both minimum legal values) can strand the buffer at a capacity it can never scale down from again — closing R18/R19, the last known instance of this ADR's threshold-collapse class.

### Negative Consequences

* The reaction delay inherent to the median remains unsolved until data justifies further investment.
* Until the calculation is extracted into a pure function, the scale-down logic remains hard to test in isolation.
* *(2026-08-21 Rodada 4 amendment)* With `minCapacity == 2`, scale-down to the floor now tolerates one active item instead of requiring exact total idleness - a real, if narrow, behavior change for that one configuration (not merely a documentation fix): a buffer that would previously never have scaled below its off-tier/max landing spot can now do so slightly sooner than before.

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
* Related: `TODO/relatorio-viabilidade-ringbufferplus-v5.md`, finding R16 (Rodada 3) — the audit that surfaced the 2026-08-21 amendment, including the exact off-tier scenario reproduced and the pre-existing `initialCapacity = 2` gap registered alongside it.
