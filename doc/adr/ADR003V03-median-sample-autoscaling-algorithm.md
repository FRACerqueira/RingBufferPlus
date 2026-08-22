<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Median-sample autoscaling algorithm|
|Version|03|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-22)|
|Changed|Accepted (2026-08-22)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Autoscale algorithm: percentile + regression, replacing the median

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision on 2026-08-22, under the [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate for v6.0.0.

Technical Story: [ADR003V01](./ADR003V01-median-sample-autoscaling-algorithm.md) kept the median algorithm explicitly for lack of evidence, and required as follow-up "(2) create a benchmark project (BenchmarkDotNet) measuring reaction time to synthetic load changes" before revisiting. [ADR003V02](./ADR003V02-median-sample-autoscaling-algorithm.md) fixed threshold-collapse bugs in the median's scale-down evaluation but did not touch the underlying statistic. This version supplies the evidence ADR003V01 asked for and were never produced until this analysis, and acts on it.

## Context and Problem Statement

Two pieces of real evidence were produced during the v6.0.0 design analysis: (1) `AutoScaleReactionBenchmarks` (already shipped in `develop`, actually run against it) shows the current reactive path's ~77ms mean reaction time is ~65% just the configured `AcquireTimeout` elapsing, not the cost of scaling — addressed separately in [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)'s backlog-based reactive signal, not this ADR. (2) A new, standalone decision-quality simulation (`benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs`, run via `dotnet run -c Release -- --algo-comparison`) ports the real `AutoScaleDecision` median logic verbatim and compares it against a percentile-plus-regression alternative across four synthetic demand traces (step up, step down, spike-and-recover, flat-but-noisy), isolating the slow/corrective decision layer from the (now shared, unchanged-between-arms) reactive path. Does this evidence justify the swap ADR003V01/V02 declined to make without data?

## Decision Drivers

* ADR003V01's own stated bar for revisiting this decision is data, not "since breaking changes are authorized, might as well" (a standard [ADR006V01](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) already applied to this exact algorithm and reaffirmed).
* The simulation shows the real median algorithm can get **structurally stuck**: in the simulated scenarios it never fully descended after a demand drop (stuck at an intermediate tier), and stayed pinned at `MaxCapacity` in two other scenarios because its scale-down comparison is strict `>`, which a plausible steady demand level can permanently fail to clear.
* The percentile+regression alternative converges far faster and right-sizes tighter in every simulated scenario, directly serving the product's "resource-conscious usage" pitch better than the current algorithm does.
* The percentile+regression alternative's raw form is genuinely worse in one respect: it oscillates heavily (48 direction changes in 300 ticks) under flat-but-noisy demand, confirming a real risk, not just a theoretical one.
* A pure statistical swap introduces new configuration surface (window size, percentile, safety buffer, prediction horizon) that did not exist before — this needs to be weighed, not waved away.

## Considered Options

* Keep the median-of-samples algorithm (ADR003V01/V02's standing decision), on the grounds that no sufficient evidence has yet been produced.
* Replace it with sliding-window percentile + safety buffer ("fair level"), adjusted by a linear-regression trend projected a configurable horizon ahead, clamped to `[min, max]`.
* Make the scale-decision algorithm pluggable (still on the table from ADR003V01, never chosen) — let the consumer choose the strategy.

## Decision Outcome

Chosen option: "Replace the median with percentile + regression", with two refinements the simulation itself proved necessary before this could be accepted as a straight upgrade rather than a different set of trade-offs:

* **Deadband**: the target does not move for changes smaller than the safety buffer's own noise tolerance. Without it, the simulation measured 48 direction changes in 300 ticks under flat-but-noisy demand; with it, 1 — with no measurable cost to convergence speed or right-sizing in any other scenario.
* **Window reset while a reactive episode is in flight**: the sample window is paused (not merely not-updated) for as long as demand keeps pace with capacity (not just on the instant a reactive trigger fires), and is cleared the moment that episode ends. This mirrors a real, already-shipped `develop` behavior described in its own `CHANGELOG.md` ("sample window resets across a scale operation, and ticks are skipped while one is in flight") rather than inventing new machinery. Measured effect: post-burst convergence dropped from 18 ticks to 1 in both affected scenarios, with lower average over-provisioning in every scenario and no oscillation regression in the noisy scenario.

The pluggable-strategy option remains not chosen, for the same reason ADR003V01 gave it: it adds public surface for a flexibility need that has not been demonstrated, independent of which single algorithm is chosen as the default.

**Parameters** (defaults, all configurable): window size, percentile `p` (default 0.95), safety buffer (default 10%), prediction horizon (default 5 sampling ticks), deadband (derived from the safety buffer's own tolerance, not a free-standing parameter).

### Positive Consequences

* Satisfies the evidentiary bar ADR003V01 itself set, with real code (the ported median) and real numbers (the simulation's output), not intuition.
* Removes a structural failure mode of the shipped algorithm (getting stuck at a coarse tier) that no threshold-formula patch (ADR003V02's Round 3/4 amendments) fully closes, because it is inherent to comparing a statistic against a fixed tier rather than sizing continuously.
* The deadband and window-reset refinements are both cheap (no new actor, no new thread) and each independently measured to remove a real regression the raw formula would otherwise have shipped with.

### Negative Consequences

* Genuinely more configuration surface than the median (window size, percentile, buffer, horizon vs. the median's implicit window/threshold pair) — none of these have been tuned against real production traffic, only synthetic scenarios; defaults should be treated as a starting point, not a validated final answer.
* The evidence is a simulation across four hand-designed synthetic scenarios and one parameter set, not a production trial — real workloads may expose behavior these scenarios did not.
* This is an observable behavioral change for any consumer relying on the median's specific timing characteristics, same class of change [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) already treats as relevant even without a signature break.

## Pros and Cons of the Options

### Keep the median (rejected, reversing ADR003V01/V02)

* Good, because it carries zero risk of new regressions.
* Bad, because the evidence now shows a real, structural failure mode (getting stuck over-provisioned) that no amount of threshold tuning fully closes.

### Percentile + regression with deadband and window-reset (chosen)

* Good, because it is backed by real, run evidence satisfying the bar ADR003V01 itself set.
* Good, because the two refinements needed to make it safe were each independently measured, not assumed.
* Bad, because it adds real configuration surface with no production-traffic validation yet.

### Pluggable strategy (still not chosen)

* Good, because it gives maximum flexibility to advanced consumers.
* Bad, because it adds public API surface for a need that remains undemonstrated, independent of which default algorithm ships.

## Links

* Supersedes (the "keep median" conclusion only; the Round 3/4 threshold-collapse fixes remain historically valid): [ADR003V01](./ADR003V01-median-sample-autoscaling-algorithm.md), [ADR003V02](./ADR003V02-median-sample-autoscaling-algorithm.md).
* Authorized by: [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — v6.0.0 breaking-change mandate.
* Related: [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) — the Monitor role that runs this algorithm, and the separate backlog-reactive signal this ADR does not change.
* Related: `benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs` — the evidence this decision is based on, runnable via `dotnet run -c Release -- --algo-comparison`.
