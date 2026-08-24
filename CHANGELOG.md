# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/) — with two documented exceptions: **v5.0.0 is a deliberate, single "clean slate" reset** with no deprecation bridge from v4.x, authorized by [ADR006](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md); and **v6.0.0 is a second, independently justified reset**, authorized by [ADR006 V02](doc/adr/ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — the last such reset without its own fresh justification (see the Unreleased section below). Strict SemVer with a deprecation cycle otherwise applies from v6.0.0 onward (see [ADR004 V03](doc/adr/ADR004V03-semantic-versioning-policy-and-fluent-api-stability.md)). A v5.1.0 minor release was planned at one point (see [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)) but was discontinued before shipping once the v6.0.0 overhaul superseded it (see [ADR006 V02](doc/adr/ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)'s Decision Outcome) — v6.0.0 is the sole, direct successor to v5.0.0, and no v5.1.0 release exists or will exist.

## [6.0.0] - 2026-08-24

A complete, coordinated product overhaul authorized by [ADR006 V02](doc/adr/ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) - strict SemVer resumes from v6.0.0 onward (superseding the v5.0.0 exception recorded in [ADR006 V01](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)/[ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)); this is not a standing precedent for future majors. Two ADRs drive this release: [ADR001 V03](doc/adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)/[ADR003 V03](doc/adr/ADR003V03-median-sample-autoscaling-algorithm.md) (the concurrency model and autoscale algorithm) and [ADR007 V03](doc/adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md) (the remaining public-surface cleanup). This release also went through a 12-round pre-release audit across correctness, resilience, usability, complexity, performance, and observability; see [doc/audits/v6.0.0-pre-release-audit.md](doc/audits/v6.0.0-pre-release-audit.md) for the full method and findings.

### Breaking changes

- **Concurrency model redesigned around four cooperating roles** (ADR001 V03): Orquestrador (sole owner of capacity state, dispatches at most one scale batch of any kind at a time), Fábrica (bounded-concurrent, backgrounded item creation), Remoção (backgrounded item disposal on scale-down, mirroring Fábrica), and Monitor (a pure, stateless predictive algorithm - see below). Both scale-up and scale-down execution now run off the engine's own single-consumer thread, so a slow/hung factory call or item disposal no longer delays every other queued command (a heartbeat replacement, the floor guard, another scale request).
- **`ElasticCapacity` gained `maxConcurrentFactoryCalls`** (default 4, `RingBufferDefault.MaxConcurrentFactoryCalls`): a creation batch's factory calls now run with bounded concurrency instead of strictly one at a time, mitigating a large batch (warmup, scale-up, floor-guard replenishment) flooding a struggling-but-technically-accepting downstream with simultaneous connection attempts. "Consecutive" in `maxConsecutiveFactoryFailures` no longer has an exact, ordered meaning under this concurrency - approximated as a single shared counter, the same "simple, not a circuit-breaker" looseness the ADR calls for.
- **Old fault-count autoscale trigger replaced by a backlog-reactive signal**: a caller that cannot be served immediately now reports itself as real-time demand the instant it starts waiting, well before `AcquireTimeout` could ever elapse - proportional to the actual unmet demand (waiting callers minus idle items), not a coarse jump on a raw fault count. `AutoScaleAcquireFault(numberOfFaults)`'s `numberOfFaults` became inert as part of this change (later removed entirely, see below).
- **Autoscale algorithm replaced** (ADR003 V03): the old median-of-idle-samples algorithm (scale-down only) is replaced by the Monitor - a sliding-window percentile "fair level" (inflated by a safety buffer) adjusted by a linear-regression trend projected a configurable horizon ahead, clamped to `[MinCapacity, MaxCapacity]`. New capability: the Monitor can scale *up* predictively, from a rising-but-still-below-capacity demand trend alone - structurally impossible for the old algorithm. Tunable via the new `MonitorTuning(percentileP, safetyBuffer, horizon, deadband)` builder method (defaults 0.95, 0.10, 5, 3).
- **`AutoScaleAcquireFault`/`IRingBufferAutoScaleBuilder<T>` removed entirely** (ADR007 V03): the floor guard, backlog-reactive signal, and Monitor are now unconditionally active for every elastic pool - there is no more "automatic vs. manual" mode to opt into or exclude. `MonitorTuning(...)` moved onto `IRingBufferElasticBuilder<T>`, the only elastic builder now.
- **`SwitchToAsync` redefined as a temporary pin**: `SwitchToAsync(ScaleSwitch value, TimeSpan pinDuration)` now requires an explicit, non-optional duration (no default - this surface had already shipped two silent, unbounded traps in earlier versions). For that duration, it substitutes for the Monitor's own predictive output; the floor guard and backlog-reactive signal are never suppressed by an active pin. A pin is not itself cancellable or shortened early - a second `SwitchToAsync` to the same capacity while a longer pin is still active is rejected as a no-op, so only real time clears an active pin.
- **`BackgroundLogger` removed entirely** (`IRingBufferBuilder<T>`/`IRingBufferFixedBuilder<T>`/`IRingBufferElasticBuilder<T>`): logging is now always synchronous, inline, on whichever thread triggered it - no dedicated background pump, no internal queue. It duplicated a problem the `ILogger` ecosystem already solves generically (async-capable providers), and was itself a repeat source of real bugs (a message-ordering/drop bug, and a throwing `OnError` permanently faulting the pump and silently dropping every later message).
- **`OnError` simplified**: from `Action<ILogger?, Exception>` to `Action<Exception>` - the logger is already configured separately via `Logger(ILogger?)`, so it is no longer also handed to the error callback.
- **`WarmupRingBufferAsync` removed**: `AddRingBuffer<T>` now also registers an `IHostedService` for the given `buffername`, which calls `WarmupAsync` automatically during the host's own startup (`StartAsync`, using that call's own token) - warmup is no longer a separate opt-in step. This fixes both root-level bugs the old extension had (an ignored token on one path, a dead null-check) by construction, via the idiomatic hosting contract, rather than patching the old signature. There is no way to opt out of automatic warmup via `AddRingBuffer<T>` itself; build the buffer directly via `RingBuffer<T>.New(...)` instead if you need that.
- **`HeartBeat` redesigned**: from `Action<RingBufferValue<T>>` to `Func<T, bool>`. The framework now owns acquiring and returning the heartbeat item; the callback receives the raw value and returns `true` to keep it or `false` to discard it (a replacement is created in its place, through the same path a caller's own `Invalidate()` uses) - there is no longer a disposable object handed to the callback to misuse (the previous "do not dispose this yourself" documented trap is gone by construction). A callback that blocks past its own `pulse` budget is still treated as a timeout regardless of what it would have returned; a thrown exception does not discard the item (same as returning `true`) - only an explicit `false` does.
- **`ElasticCapacity`'s parameters reshaped**: `minCapacity`/`maxCapacity` are now the first two (required) parameters, and the former `initialCapacity` is now a `target` parameter - the third, and now **optional** (defaults to `minCapacity`, "provision for demand, not worst case" - start small and let the floor guard/backlog-reactive signal/Monitor grow it, rather than an arbitrary middle default). `minCapacity == maxCapacity` is a valid, degenerate configuration (no real elasticity) rather than an error; an explicitly-given `target` must still fall within `[minCapacity, maxCapacity]` (so it must equal both in that case), enforced by the same validation `Build`/`BuildWarmupAsync` already ran for `initialCapacity`. Every positional call site changes shape: `ElasticCapacity(initialCapacity, minCapacity, maxCapacity, ...)` becomes `ElasticCapacity(minCapacity, maxCapacity, target, ...)`.

### Added

- A simple, internal growing backoff after consecutive genuine factory failures (100ms base, doubling, capped at 5s, reset on any success) - not exposed as builder configuration, a small fixed policy rather than a new tunable.
- The floor guard: if `CurrentCapacity` ever drops below `MinCapacity` (only reachable today via a failed heartbeat- or `Invalidate()`-triggered single-item replacement), it is replenished immediately and undebounced, the highest-priority signal of all. An elapsed-grace-window (`FactoryTimeout`, reused, no new configuration) is logged if a breach outlives one full cycle without recovering.

### Fixed

- `Invalidate()`'s own replacement is no longer at risk of never being queued if the invalidated item's own `Dispose()`/`DisposeAsync()` hangs forever - the replacement is now enqueued unconditionally before that disposal is awaited, not after it in a `finally` that a hung dispose would never reach.
- The Monitor's `deadband` default (3) can no longer make `MinCapacity`/`MaxCapacity` permanently unreachable when the buffer's own span is smaller than the deadband itself (e.g. `ElasticCapacity(4, 2, 8)`) - the deadband is now capped to the maximum delta actually reachable in the requested direction.
- Corrected an inverted doc claim about which `ElasticCapacity` parameter controls the Monitor's reaction horizon: it is `baseTimer`, not `numberSamples` - the sliding window's real-time span is always exactly `baseTimer` regardless of `numberSamples`.
- A builder configured with `OnError(...)` but no `Logger(...)` now actually has its error handler invoked during `Build`/`BuildWarmupAsync` validation failures - previously the guard required a non-null, Error-enabled `Logger` before even checking whether an `OnError` handler existed, silently skipping it otherwise.
- A cached warmup failure ([ADR011 V01](doc/adr/ADR011V01-retry-path-for-a-failed-warmup-async-instead-of-a-permanently-broken-instance.md)) no longer silences telemetry on every subsequent implicit `AcquireAsync` call - each now still records an `acquire.duration` row and `Activity`, disambiguated from an ordinary shutdown race via a new `acquire.warmup_failed` tag.
- The floor guard's "below minimum capacity" error no longer repeats on every retry evaluation once its grace window has elapsed - it now reports once, then again only after a further full grace window if the breach persists, a bounded fallback cadence instead of an unbounded one.
- A `HeartBeat` callback's "unhealthy" verdict (triggering an item replacement) is now observable on its own via a new `ringbufferplus.heartbeat.invalidations` counter and log message, instead of being indistinguishable from a healthy pump iteration.
- The `acquire.*`/`scale.*` trace `Activity`s now carry the same `success`/`warmup_failed` tags already present on their corresponding metrics - the two signals had drifted out of sync for several outcome paths.

### Known issues

- `SwitchToAsync`'s unlocked path has no per-call telemetry of its own (it never had one); factory attempts made on that path are still visible via the existing per-attempt logging.
- A gauge registered in the constructor can observe capacity state before the constructor has fully returned; documented, not fixed, as a narrow and harmless ordering window.
- The Monitor's `active` condition is broader than "genuine backlog while pinned at `MaxCapacity`" - it also holds under ordinary full utilization with no waiters, silencing a sampling tick in that case too. Narrowing it would be a real autoscale algorithm change requiring the existing decision-quality simulation tool to model `idle`/`waiting` separately first; deliberately deferred, not a defect in this release. See [the audit report](doc/audits/v6.0.0-pre-release-audit.md#a-deliberately-deferred-design-question).

## [5.0.0] - 2026-08-12

v5.0.0 is a complete, coordinated product overhaul with sweeping breaking changes — see the ADRs in [doc/adr](doc/adr/indexadrs.md) for full context.

### Breaking changes v5.0.0

- Concurrency core rewritten on `System.Threading.Channels` as a single state machine ([ADR001](doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)). All `RingBufferManager<T>` mutable scale state is now owned exclusively by one consumer loop — no lock/semaphore is used or needed.
- `IDisposable` removed; `IAsyncDisposable` becomes the sole disposal contract on `IRingBufferService<T>` and `RingBufferValue<T>` ([ADR005](doc/adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)). `using`/`Dispose()` call sites must become `await using`/`DisposeAsync()`.
- **Breaking:** disposal is no longer triggered automatically when the constructor's lifetime `CancellationToken` is cancelled (the v4 `Register(() => Dispose())` reentrancy hazard is removed entirely). Code that relied on `cts.Cancel()` alone to tear a buffer down (outside of DI, where the `ServiceProvider` already calls `DisposeAsync` on `IAsyncDisposable` singletons) must call `DisposeAsync()` explicitly.
- Public fluent builder surface redesigned around explicit, mutually exclusive `FixedCapacity`/`ElasticCapacity` modes, replacing `Capacity`/`ScaleTimer`/`MinCapacity`/`MaxCapacity` ([ADR007](doc/adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)). `AutoScaleAcquireFault(...)` now returns a builder whose `Build()`/`BuildWarmupAsync()` produce a plain `IRingBufferService<T>` with no `SwitchToAsync` — manual switching and autoscale-on-fault are mutually exclusive at the type level, not a silent runtime `false`.
- **Breaking:** the three v4 builder interfaces were renamed and split into four. Update any explicitly-typed variable, field, or helper signature that names one of these:
  - `IRingBuffer<T>` → `IRingBufferBuilder<T>` (the entry point returned by `RingBuffer<T>.New(...)`)
  - `IRingBufferBuild<T>` → `IRingBufferFixedBuilder<T>` (fixed capacity)
  - `IRingBufferScaleCapacity<T>` → `IRingBufferElasticBuilder<T>` (elastic capacity, manual switching) or `IRingBufferAutoScaleBuilder<T>` (elastic capacity, autoscale-on-fault — a new split, not a v4 type)
- **Breaking:** `SwitchToAsync` no longer exists on `IRingBufferService<T>` at all — not only when autoscale-on-fault is enabled. It moved to the new `IRingBufferManualScaleService<T>` (returned by `.ElasticCapacity(...).Build()`/`.BuildWarmupAsync()` when autoscale-on-fault is *not* used). Code that held an `IRingBufferService<T>` field and called `SwitchToAsync` on it directly must either hold the more specific `IRingBufferManualScaleService<T>` type where possible, or pattern-match: `if (service is IRingBufferManualScaleService<T> manual) { ... }` (see the [DI guide](doc/guides/usage-dependency-injection.md#trade-offs--limitations)).
- **Breaking:** `AcquireTimeout`'s `delayAttempts` parameter and `RingBufferDefault.AcquireDelayAttempts` are removed — a `Channel`-based acquire has no polling loop to pace.
- **Behavior change:** acquire no longer blocks while a scale operation is in progress, regardless of `LockWhenScaling` — items already in the pool are always immediately acquirable. `LockWhenScaling` now controls exactly one thing: whether `SwitchToAsync`'s caller awaits the scale operation's completion before returning.
- **Behavior change:** autoscale-on-fault counts *timed-out acquires*, not per-poll-attempts-within-one-acquire-call as in v4. Where `AcquireTimeout` is large relative to `AutoScaleAcquireFault`'s threshold, scale-up now reacts on the order of `AcquireTimeout × numberOfFaults`, not near-instantly — tune `AcquireTimeout` down if fast autoscale reaction is required.
- `WarmupRingBufferAsync` signature fixed (the `token` parameter is now honored; missing-buffer now throws `ArgumentNullException` as documented, instead of silently no-oping).
- **v4.x and earlier receive no further fixes now that v5.0.0 has shipped** — including the concurrency bugs listed under v4.0.1 below. See [ADR004](doc/adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) and `SECURITY.md`.

### Added

- Native observability: every buffer now emits OpenTelemetry-compatible metrics (`System.Diagnostics.Metrics.Meter`, name `"RingBufferPlus"`) and traces (`System.Diagnostics.ActivitySource`, same name) — `acquire.duration`, `acquire.faults`, `capacity.current`, `scale.operations`, `scale.duration`, and one `Activity` per acquire/scale operation, all tagged `buffer.name` ([ADR008](doc/adr/ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)). No new dependency: both APIs ship in the .NET shared framework since .NET 5, and RingBufferPlus does not depend on the `OpenTelemetry` package itself — see the [observability guide](doc/guides/usage-observability.md).

## [4.0.1] - 2025-11-14

### Added
- .NET 10 support.

### Known issues (tracked for the v5.0.0 rewrite, not fixed in this line)
- Concurrency bugs in `RingBufferManager` (semaphore over-release on cancellation, duplicate-warmup race, `Dispose()` reentrancy) — documented in [ADR001](doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md). No backport is planned; see [ADR004](doc/adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md).

## [4.0.0] - 2025-04-09

### Added
- .NET 9 support (.NET 8 support maintained).
- `LockWhenScaling` command.
- `AutoScaleAcquireFault` command.
- `HeartBeat` command.
- `BackgroundLogger` command.

### Changed
- **Breaking:** some properties and commands refactored for readability/consistency.
- **Breaking:** renamed `ScaleUnit` to `ScaleTimer`.
- Several commands optimized for performance and consistency during auto/manual scaling.
- Several commands made asynchronous.
- Documentation updated.

### Removed
- .NET 6, .NET 7, and netstandard2.1 support.
- **Breaking:** removed the Master/Slave, `ReportScale`, `BufferHealth`, `ScaleWhen...`, `RollbackWhen...`, and `TriggerByAccqWhen...` concepts.

### Fixed
- RabbitMQ usage bug — no longer necessary to disable Automatic Recovery.

## [3.2.0] - 2023-12-04 (deprecated)

### Added
- `Slave` command to set a Slave RingBuffer.
- `SwithTo` command to manually switch scale when scale type is manual.

### Changed
- Renamed `MasterScale` to `ScaleUnit`, adding a `ScaleUnit` parameter for scale type (automatic/manual/Slave).
- Downscaling no longer needs to remove the entire buffer when there is no slave control — better performance and availability.

### Removed
- `SlaveScale` command — use `ScaleUnit` with the Slave type instead.
- `SampleUnit` command — time base unit and sample count are now parameters of `ScaleUnit`.
- Mandatory `ScaleWhenFreeLessEq`/`RollbackWhenFreeGreaterEq` commands for `MaxCapacity` — now set automatically.
- Mandatory `ScaleWhenFreeGreaterEq`/`RollbackWhenFreeLessEq` commands for `MinCapacity` — now set automatically.

## [3.1.0] - 2023-12-01

Initial tagged history predates this changelog. See the [GitHub releases](https://github.com/FRACerqueira/RingBufferPlus/tags) and repository history for details.
