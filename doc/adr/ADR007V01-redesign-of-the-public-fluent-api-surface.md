<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Redesign of the public fluent API surface|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Redesign of the public fluent API surface

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision confirmed on 2026-08-11: option "Collapse interfaces with explicit, type-level modes".

Technical Story: The original architecture review had already listed the fluent API's 3 chained interfaces (`IRingBuffer<T>` → `IRingBufferScaleCapacity<T>` → `IRingBufferBuild<T>`) as a trade-off whose only cost was "widens the cost of any breaking change — history shows renames on every major". With the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate removing any commitment to the current version, this is the one item in the entire original analysis whose blocker was *purely* compatibility cost — and that blocker has just been removed. There was no ADR of its own for it; this ADR closes that gap.

## Context and Problem Statement

The current fluent API (`src/RingBufferPlus/Commands/*.cs`, `Core/RingBufferBuilder.cs`) has concrete surface defects, independent of any implementation bug:

1. `Capacity(value)` silently sets `_initcapacity = _maxCapacity = _minCapacity = value` — the method's name/doc ("Sets the initial/startup capacity") does not warn that it also resets min/max, and subsequent calls to `ScaleTimer()...MinCapacity()/MaxCapacity()` need to know this to refine correctly.
2. `ScaleTimer(numberSamples, baseTimer)` bundles two roles under one name: it is the *mode switch* that enables elastic mode (it is the only way to reach `IRingBufferScaleCapacity<T>`, where `MinCapacity`/`MaxCapacity` live) **and** it is the setter for the autoscale sampling parameters. The name does not clearly suggest either role.
3. `AutoScaleAcquireFault(...)` silently disables `SwitchToAsync` (which then always returns `false` — see `RingBufferManager.cs:238-241`) with no compile-time signal that the two commands are mutually exclusive.
4. `LockWhenScaling` broadly changes the blocking semantics of `AcquireAsync`/`SwitchToAsync` (documented in README prose), but is just one more boolean among several builder methods — the weight of that decision is not communicated by the API's shape.
5. Historical evidence: this exact surface has already been the target of 3 rounds of renaming/removal between v3.2.0 and v4.0.0 (`MasterScale`→`ScaleUnit`→`ScaleTimer`, removal of `Master/Slave`, `ReportScale`, etc.) — a signal that the current shape has not yet converged.

Since the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate removes compatibility as a constraint for the first time, this is the only window in which the public surface itself — not just the internal implementation — can be redesigned without that cost.

## Decision Drivers

* Historical churn concentrated exactly on this surface across previous majors — evidence of a non-converged design, unlike [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md) (where there is no evidence of a problem).
* Builder methods today conflate unrelated concerns under a single name (`ScaleTimer` = mode switch + sampling parameter).
* Silent runtime interactions between commands (`AutoScaleAcquireFault` turns off `SwitchToAsync`) are hard to discover without reading the source.
* The [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate already requires every consumer to migrate for other reasons (concurrency, disposal) — grouping the surface fix into the same migration event does not increase the number of breaking-change events, only the size of one event that is already unavoidable.
* The product's pitch ("Simple and clear fluent syntax") is a real feature to preserve, not discard out of inertia.

## Considered Options

* **Keep it as is** (3 chained interfaces, same set of methods) — only fix names/docs for the 4 defects without changing structure.
* **Collapse into fewer interfaces with explicit, mutually exclusive, type-level modes** — e.g. a single `IRingBufferBuilder<T>` (or at most 2 interfaces), clearly separating `.FixedCapacity(n)` from `.ElasticCapacity(min, max)` as distinct methods (instead of `Capacity()` + `ScaleTimer()` inferring the mode), and making the `AutoScaleAcquireFault` vs. `SwitchToAsync` exclusivity visible in the returned type (e.g. `AutoScaleAcquireFault(...)` returns a type that no longer exposes the manual-switch equivalent).
* **Options-object configuration** (`RingBufferOptions<T>` with properties/records, passed to a single `Build(options)`), abandoning the fluent chain in favor of the `IOptions<T>` pattern already idiomatic in modern .NET.

## Decision Outcome

**Decided on 2026-08-11 by the maintainer:** "Collapse into fewer interfaces with explicit, mutually exclusive, type-level modes" (option 2 — this ADR's original recommendation), because it resolves the 4 concrete defects from the context (silent overwrite, overloaded name, uncommunicated exclusivity, hidden weight of `LockWhenScaling`) without abandoning the "fluent syntax" product pillar. The options-object option (option 3) remains registered as a non-chosen alternative — it would abandon a currently documented differentiator for no reason beyond style preference.

**Concrete design:**
* `.FixedCapacity(n)` and `.ElasticCapacity(min, max)` as distinct builder methods, replacing the implicit inference today done by `Capacity()` + `ScaleTimer()` — each returns a type that only exposes what makes sense for that mode (fixed capacity does not expose `MinCapacity`/`MaxCapacity`/`AutoScaleAcquireFault`).
* `AutoScaleAcquireFault(...)`, when called, returns a type that **no longer** exposes the manual `SwitchToAsync` equivalent — the exclusivity becomes a compile-time error, not a silent runtime `false`.
* `LockWhenScaling` remains an explicit method, but only available from `.ElasticCapacity(...)` (it makes no sense in fixed mode), making it visible in the type that this is an elastic-mode-specific decision.
* `Capacity(value)` no longer exists under that name/dual-effect; `FixedCapacity(n)` communicates on its own that it sets a single value, with no side effect on min/max.

### Positive Consequences

* Fixes the 4 surface defects at their root (type/name), not only in documentation.
* `AutoScaleAcquireFault`/`SwitchToAsync` exclusivity becomes a compile-time error, not a silent runtime `false`.
* Consolidates into the same migration event already required by [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)/[ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) — it does not create a second breaking-change cycle.

### Negative Consequences

* Widens the scope of the CHANGELOG's breaking-changes entry (no dedicated migration guide, see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)'s revision note) — every builder call site in samples and in external consumers needs to be rewritten, regardless of which option is chosen.
* Type redesign carries a risk of introducing new ambiguity if mode exclusivity is not modeled carefully (e.g. exploding into one interface per flag combination).

## Pros and Cons of the Options

### Keep it as is, only fix names/docs (rejected)

* Good, because it is the smallest change scope.
* Bad, because it does not resolve the silent `AutoScaleAcquireFault`/`SwitchToAsync` interaction nor the silent `Capacity()` overwrite — it only documents the defects, it does not eliminate them.

### Collapse interfaces with explicit, type-level modes (chosen)

* Good, because it fixes the 4 defects at their root.
* Good, because it preserves the "fluent syntax" product pillar.
* Bad, because it requires careful design to avoid trading "3 confusing interfaces" for "N interfaces, one per mode combination" — mitigated by the concrete design listed in the Decision Outcome (2 modes, not N).

### Options object (`RingBufferOptions<T>`) (not chosen)

* Good, because it is the most idiomatic pattern in modern .NET (`IOptions<T>`) and eliminates any call-order ambiguity.
* Good, because it simplifies validation (everything in one object, validated at once in `Build`).
* Bad, because it abandons the "fluent syntax" product pillar for no reason beyond style preference — the largest departure from the current product among the three options.

## Links

* Refined by: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — mandate that removes the compatibility cost that blocked this redesign.
* Related: [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — the final builder constructs the rewritten `RingBufferManager`; the builder's shape and the manager's shape should be designed together.
* Related: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — this surface change is one of those covered by stage (a) — the single v5.0.0 reset.
