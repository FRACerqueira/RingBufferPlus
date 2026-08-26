<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Redesign of the public fluent API surface|
|Version|03|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-22)|
|Changed|Accepted (2026-08-22)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Remaining public surface for v6.0.0: explicit target capacity, pinned manual scale, and surface cleanup

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision on 2026-08-22, under the [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate for v6.0.0.

Technical Story: [ADR007V02](./ADR007V02-redesign-of-the-public-fluent-api-surface.md) collapsed the builder into explicit, type-level modes (`FixedCapacity`/`ElasticCapacity`) and removed `LockWhenScaling` as a proven no-op trap. This version covers what that redesign did not: an explicit target-capacity concept, `SwitchToAsync`'s role once automatic scaling is always-on (not a togglable mode, see [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)), and the remaining surface items [ADR006V01](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) already flagged as root-level bugs (`HostingExtensions.WarmupRingBufferAsync`'s ignored token and dead null-check) plus items raised fresh during this analysis (`HeartBeat`'s disposal trap, `BackgroundLogger`'s shared-queue risk, a polling-loop parameter with nothing left to configure).

## Context and Problem Statement

v6.0.0's concurrency model ([ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)) makes the floor guard, backlog-reactive signal, and Monitor always active for any elastic pool — there is no more "automatic vs. manual" mode to switch between, unlike v4/v5's mutually-exclusive `AutoScaleAcquireFault`/`SwitchToAsync` relationship. What does manual scaling mean now, what does the initial-capacity concept need to be to support it cleanly, and what happens to the handful of remaining surface items nobody had revisited since v4?

## Decision Drivers

* `Capacity(value)` (v4) silently set init/min/max together — already named as a defect in ADR007V01; a `target` concept needs to not repeat that mistake.
* This project has twice shipped a silent behavioral trap on this exact surface (`AutoScaleAcquireFault` silently disabling `SwitchToAsync` in v4; `LockWhenScaling` surviving as a documented no-op until ADR007V02's amendment) — a third instance must be actively designed against, not risked again.
* `HeartBeat`'s current design hands the callback a disposable object it must not dispose — the same shape of trap, on a different member.
* `BackgroundLogger` reintroduces a shared cross-thread queue, the exact class of primitive ADR001 identified as a real bug source in `main`, to solve a problem (slow log sinks) the standard `ILogger` ecosystem already solves generically.
* `HostingExtensions.WarmupRingBufferAsync`'s bugs (ignored token, dead null-check) were already named in ADR006V01 as needing a root-level fix via this surface redesign, not a patch.

## Considered Options (per surface item)

**Capacity model**: keep `Capacity`/`MinCapacity`/`MaxCapacity` as in ADR007V01/V02, vs. add an explicit `target` as a third named value.

**Manual scale (`SwitchToAsync`)**: keep it as a mode mutually exclusive with automatic scaling (the v4/v5 shape), vs. remove it entirely (automatic scaling is always on, no override), vs. redesign it as a temporary override ("pin") of the Monitor's predictive output.

**`HeartBeat`**: keep the current `Action<RingBufferValue<T>>` shape with its documented "do not dispose this" caveat, vs. redesign around a health-check result the framework interprets itself.

**Logging**: keep `BackgroundLogger` as a separate opt-in queue, vs. remove it and rely on `ILogger`'s own async-capable providers.

**DI/hosting**: patch `WarmupRingBufferAsync`'s two bugs in place, vs. replace it with an `IHostedService`.

## Decision Outcome

* **`target` capacity**: added as an explicit third parameter alongside `min`/`max` (default = `min` in elastic mode, matching "provision for demand, not worst case"). `min == max` means no elasticity; `target`, if given, must equal both, else a configuration error — extending, not reopening, ADR007V01/V02's validation-at-configuration-time principle. [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)'s `MaxConcurrentFactoryCalls` is exposed on the same `ElasticCapacity(...)` builder call, alongside `target`/`min`/`max` — it is not a separate command.
* **`SwitchToAsync` as a temporary pin, with a mandatory duration**: it substitutes for the Monitor's predictive output for an explicit, required duration — no default, because this surface has already produced two silent, unbounded traps (see Decision Drivers) and a third was judged unacceptable. The floor guard and backlog-reactive signal are never suppressed by an active pin: real waiting callers and the safety floor always win regardless of what an operator pinned earlier. `LockWhenScaling` (reintroduced after ADR007V02's removal, once ADR010V01 gave a partial scale a well-defined `bool` outcome to wait for) stays available on `IRingBufferElasticBuilder<T>` and behaves the same as before this ADR: it only changes what `SwitchToAsync`'s caller waits for, never what `AcquireAsync` does.
* **`HeartBeat` redesigned**: signature changes from `Action<RingBufferValue<T>>` to `Func<T, bool>`. The framework acquires and returns internally; a `false` result is interpreted through the same `Invalidate()` path any consumer uses — one mechanism, not two, and no disposable object handed to code that must not dispose it.
* **`BackgroundLogger` removed**: `.Logger(ILogger?)` stays as a plain sink, called inline from whichever role needs to log; no shared queue, no dedicated thread. A consumer needing non-blocking logging configures an async-capable `ILogger` provider, which is a solved, generic problem outside this library's scope.
* **`OnError` simplified**: to `Action<Exception>`, called inline, no queue. When configured, it substitutes for `Logger`'s Error-level output for that event rather than adding to it — `Logger` still receives every other level.
* **`WarmupRingBufferAsync` replaced by an `IHostedService`**: registered alongside the pool, calling `BuildWarmupAsync` with its own `StartAsync(CancellationToken)` token. This fixes both bugs ADR006V01 named at their root via the idiomatic .NET hosting contract, rather than patching the existing bespoke extension's signature.
* **`AcquireDelayAttempts` removed**: it configured the delay of a manual polling loop that no longer exists once acquire is a `Channel<T>` read gated by a linked timeout token (already true since v5.0.0's engine rewrite — this parameter was simply never removed from the surface).

### Positive Consequences

* `target` closes the same class of defect ADR007V01 already named for `Capacity(value)`, instead of only partially fixing it.
* The mandatory pin duration is a structural guard against a third instance of this exact surface's recurring silent-trap failure mode.
* `HeartBeat`'s redesign removes a disposal trap by not handing out a disposable object in the first place, rather than documenting around it.
* Removing `BackgroundLogger` and `AcquireDelayAttempts` shrinks the surface by removing machinery that either duplicates a generic solved problem or has nothing left to configure.
* `WarmupRingBufferAsync`'s replacement fixes both of ADR006V01's named bugs at the root, as that ADR itself called for.

### Negative Consequences

* Every builder call site (samples, external consumers) needs rewriting regardless of which item changed — consistent with, and not an added cost beyond, the scope ADR007V01/V02 already established for this migration event.
* `HeartBeat` and DI registration call sites specifically need rewriting even though ADR007V01/V02 did not originally flag them — this widens the CHANGELOG's breaking-changes surface for v6.0.0 beyond what ADR007V02 covered.
* No default pin duration means every manual-scale call site must decide one explicitly — a deliberate friction, not an oversight, given the surface's history.

## Pros and Cons of the Options

### `target` as explicit third parameter (chosen)

* Good, because it names a concept `Capacity(value)`'s side effect only implied.
* Bad, because it is one more thing a consumer must understand when configuring elastic mode — mitigated by defaulting to `min`.

### `SwitchToAsync` as a pin with mandatory duration (chosen)

* Good, because it keeps a legitimate operational lever (pre-scale ahead of a known event) without permanently fighting the automatic layers.
* Good, because the mandatory duration makes the override's lifetime visible instead of silent.
* Bad, because every existing manual-scale call site must now also decide a duration, with no default to fall back on.

### Remove `SwitchToAsync` entirely (rejected)

* Good, because it removes the surface that has twice produced a silent trap.
* Bad, because it removes a legitimate lever (anticipatory pre-scale) that a purely reactive/predictive system cannot fully replace.

### `HeartBeat` as `Func<T, bool>` (chosen)

* Good, because it cannot be misused the way a disposable-but-must-not-dispose object can.
* Bad, because it is a breaking signature change for any existing `HeartBeat` call site.

## Links

* Refines: [ADR007V02](./ADR007V02-redesign-of-the-public-fluent-api-surface.md) — extends the type-level-mode redesign with `target`, the manual-scale pin, and the remaining surface cleanup.
* Authorized by: [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — v6.0.0 breaking-change mandate; also closes the `HostingExtensions.WarmupRingBufferAsync` bugs that ADR006V01 originally named.
* Related: [ADR001V03](./ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) — the floor guard and backlog-reactive signals always take priority over an active pin (never suppressed); the pin, in turn, takes priority over the Monitor's predictive signal for its duration.
* Related: [ADR005V01](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) — `RingBufferValue<T>`'s exclusive `IAsyncDisposable`, reused unchanged; `Invalidate()` is the single mechanism `HeartBeat`'s redesign now routes through too.
