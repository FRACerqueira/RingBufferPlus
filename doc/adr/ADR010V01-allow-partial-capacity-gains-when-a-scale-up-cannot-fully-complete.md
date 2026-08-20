<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Allow partial capacity gains when a scale-up cannot fully complete|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-20)|
|Changed|Accepted (2026-08-20)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Allow partial capacity gains when a scale-up cannot fully complete

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: v5.0.0 product-viability audit (`TODO/relatorio-viabilidade-ringbufferplus-v5.md`), finding R5 / `TODO/plano-de-acao.md` P1#8.

## Context and Problem Statement

`CreateItemsAsync` (`RingBufferManager.cs`) builds `quantity` items for a scale-up inside a single deadline. Before this decision, if that deadline was hit — or the factory threw — after only *some* of the requested items had been created, every already-created item was disposed and `MoveToCapacityAsync` reported the whole attempt as a no-op: `_currentCapacity` stayed exactly where it started, discarding real, working resources (already-open RabbitMQ channels, already-established DB connections, etc.) that had cost real time and load on the external dependency to produce. Should a scale-up that cannot fully reach its target keep the partial progress it already made, or is all-or-nothing simpler and safer to reason about?

This decision was made alongside a separate, unrelated fix in the same method: the scale-up deadline itself was decoupled from the sampling cadence (`SamplesBase`) and now scales with the actual work requested (`quantity × FactoryTimeout`). That fix (already reduces how often a scale-up times out at all) is not part of this ADR — this ADR is specifically about what happens on the attempts that still don't fully complete, whether due to a timeout or a genuine factory failure partway through.

## Decision Drivers

* The buffer's central value proposition is resource-conscious capacity management — discarding successfully-created resources on a partial failure works directly against that, and can repeat every fault cycle under a sustained partial outage (confirmed in the audit against the shipped RabbitMQ sample: 10 real channels opened and discarded per attempt, for zero net capacity gained).
* `_currentCapacity` is documented as "current capacity of the buffer" ([ADR008](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)'s `capacity.current` gauge reads it directly) — it should reflect how many items the pool can actually serve, not an all-or-nothing flag about the most recent scale attempt.
* [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md) already establishes, for the scale-*down* side, that observable behavior changes to scaling are worth their own record even without a signature change — this decision is the scale-*up* counterpart of that same principle.
* A caller's existing way of asking "did the scale-up fully succeed" (`SwitchToAsync`'s `bool` result, or the `success` tag on `scale.operations`/`scale.duration` added in the same audit) must still mean "reached the full target" — partial progress must not be reported as success.

## Considered Options

* Keep all-or-nothing: on any timeout or mid-attempt failure, dispose everything created so far and report zero progress (status quo before this decision).
* Keep partial progress: write whatever was successfully created into the pool and advance `_currentCapacity` by that real amount, while still reporting the *attempt* as unsuccessful (`false`) if the full target was not reached.
* Keep partial progress and also report success as "partially true" (e.g., a three-state result or a separate "partial" flag on the metrics).

## Decision Outcome

Chosen option: "Keep partial progress," reporting success/failure only in terms of whether the *full target* was reached. Concretely: `CreateItemsAsync` now writes every item it managed to create into `_availableItems` before returning, on every exit path (full success, timeout, or a factory exception after at least one item was created), and `MoveToCapacityAsync` advances `_currentCapacity` by the real count gained rather than only on hitting the exact target. A factory exception is only rethrown to the caller (surfacing as a real `SwitchToAsync` exception, or a `Warmup` failure) when *nothing at all* was created — once there is real partial progress to report, the return value carries that instead, and the command is still considered handled rather than failed, so the engine loop is not disrupted by it. The three-state ("partial") reporting option was rejected as unnecessary complexity: existing callers already have a boolean success signal, and the actual count is directly observable via `CurrentCapacity` before and after — a third state would duplicate information already available through the existing contract, for no caller need identified in the audit.

### Positive Consequences

* A scale-up that gets partway there under real load or a partial outage keeps what it built instead of throwing it away — directly closes the RabbitMQ sample's "10 channels opened and discarded for zero gain, repeating every fault cycle" failure mode from the audit.
* `CurrentCapacity` (and the `capacity.current` gauge) always reflects the pool's real, currently-usable capacity, never a value that lags behind resources that already exist and are already sitting in the pool.
* No public signature changed — `SwitchToAsync`'s `bool`, `WarmupAsync`'s exception contract, and the `scale.operations`/`scale.duration` `success` tag all keep their existing meaning ("reached the full target"); only the internal bookkeeping behind them changed.

### Negative Consequences

* A caller who only checks the boolean result and never inspects `CurrentCapacity` will see `false`/an exception on a partial scale-up exactly as before, with no direct signal that *some* progress was still made — they have to know to check capacity separately if that distinction matters to them. Not a regression (the old behavior gave them nothing either), but not fully solved by this decision alone.
* Slightly more moving parts in `CreateItemsAsync`'s exception handling (the "only rethrow if nothing was created" branch) than the previous uniform "always dispose everything, always report zero" logic — traded deliberately for the resource-conservation benefit above.

## Pros and Cons of the Options

### All-or-nothing (previous behavior)

* Good, because the logic is simple: one outcome, one code path, nothing partial to reason about.
* Bad, because it destroys real, working, costly-to-create resources on every partial failure, working directly against the product's resource-conscious positioning.
* Bad, because it repeats on every fault cycle under a sustained partial outage, adding load to an already-struggling dependency for zero benefit (the exact scenario the audit reproduced against the shipped sample).

### Keep partial progress, boolean success unchanged (chosen)

* Good, because it stops discarding real capacity gains while keeping every existing public contract's meaning intact — no caller-visible signature or semantic change beyond "capacity now goes up sooner than it used to."
* Good, because `CurrentCapacity` becoming an honest, always-current number is a strict improvement with no new failure mode introduced.
* Bad, because a caller relying solely on the boolean still can't distinguish "gained nothing" from "gained some" without a second check against `CurrentCapacity`.

### Keep partial progress with three-state reporting

* Good, because it would let a caller distinguish "fully succeeded," "partially succeeded," and "gained nothing" from the return value alone.
* Bad, because it changes an existing public contract's shape (`bool` → something richer) for a distinction every caller can already reconstruct from `CurrentCapacity`, with no identified caller need for it.

## Links

* Related: [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md) — establishes that observable scaling-behavior changes are worth recording even without a signature change; this decision applies that principle to the scale-up side.
* Related: [ADR008](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md) — the `capacity.current` gauge and the `scale.operations`/`scale.duration` `success` tag whose existing meaning this decision preserves.
* Related: `TODO/relatorio-viabilidade-ringbufferplus-v5.md`, finding R5, and `TODO/plano-de-acao.md` P1#8 — the audit finding and implementation record for this decision, including the separate (non-ADR) deadline-decoupling fix made alongside it.
