# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/) — with two documented exceptions: **v5.0.0 is a deliberate, single "clean slate" reset** with no deprecation bridge from v4.x, authorized by [ADR006](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md); and **v5.1.0 carries one narrow breaking removal without a version-major bump**, authorized by [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md). Strict SemVer with a deprecation cycle otherwise applies from v5.0.0 onward (see [ADR004](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)).

## [Unreleased] — targeting 5.1.0

v5.0.0 already shipped (2026-08-12) and is not being revisited — this section is the hardening pass that follows it, produced by a v5.0.0 product-viability audit (see `TODO/relatorio-viabilidade-ringbufferplus-v5.md` and `TODO/plano-de-acao.md`). It is a **minor** release: mostly non-breaking bug fixes and behavior refinements, plus one narrow breaking removal explicitly excepted from a major bump — see [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md).

### Breaking changes

- **Breaking (semver-exempted — see [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)):** `LockWhenScaling(bool)` removed entirely from `IRingBufferAutoScaleBuilder<T>` — it was a documented no-op there ("has no observable effect") that had already misled a real caller. Any call chaining `.LockWhenScaling()` after `.AutoScaleAcquireFault()` now fails to compile instead of silently compiling and doing nothing; the fix is to delete that call, which was never doing anything for you. This removal skips both the usual `[Obsolete]` deprecation cycle and the major-version bump a public removal would otherwise require, as two explicit, narrow policy exceptions — see [ADR007 V02](doc/adr/ADR007V02-redesign-of-the-public-fluent-api-surface.md) and [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md) for the justification of each.

### Added

- `Factory(value, timeout, maxConsecutiveFactoryFailures)` gained a third, optional parameter: how many *consecutive* per-item failures a single creation batch (warmup fill, or a scale-up) tolerates before giving up on the remaining not-yet-attempted items, resetting on every success. Default is 0 — the exact same fail-fast behavior as before this parameter existed. Opt in with a higher value if your factory has occasional, recoverable hiccups you'd rather not let sink the whole batch.

### Fixed

- The engine's command loop now survives a non-cancellation exception from the user's `Factory` or item `Dispose`, instead of dying permanently and hanging every subsequent call.
- `WarmupAsync`/`SwitchToAsync` no longer hang when their command is abandoned unread in the internal command channel during a disposal race.
- Concurrent `DisposeAsync` calls on `RingBufferValue<T>`/`RingBufferManager<T>` no longer race — the dispose guard is now atomic.
- A `HeartBeat` callback that blocks past its pulse budget no longer stalls the heartbeat pump forever.
- `DisposeAsync` now always drains pooled items and disposes its own instrumentation regardless of how a background pump ended; a lease returned after disposal is now disposed instead of silently dropped.
- Scale-up's deadline now scales with the work requested (`quantity × FactoryTimeout`) instead of the unrelated sampling cadence, and a scale-up that cannot fully complete keeps the partial capacity it already gained instead of discarding it — see [ADR010](doc/adr/ADR010V01-allow-partial-capacity-gains-when-a-scale-up-cannot-fully-complete.md).
- Autoscale-on-fault no longer gets permanently stuck when `initialCapacity == minCapacity`.
- `scale.operations`/`scale.duration` now carry a `success` tag, and the `RingBufferPlus.Scale` activity now sets an error status on a failed/timed-out attempt.
- README Quickstart and the dependency-injection guide corrected to match the real v5 API and behavior.
- A failed `WarmupAsync()` no longer permanently bricks the instance: calling it again now retries from scratch instead of rethrowing the same cached failure forever — see [ADR011](doc/adr/ADR011V01-retry-path-for-a-failed-warmup-async-instead-of-a-permanently-broken-instance.md). `AcquireAsync`/`SwitchToAsync`'s implicit warmup trigger does not auto-retry on its own.
- Autoscale-on-fault now scales up on exactly `numberOfFaults` faults as documented (was off-by-one, requiring one extra fault).
- The autoscale fault counter no longer piles up unboundedly while pinned at `MaxCapacity`, and a scale-up attempt that fails or only partially completes no longer burns the whole fault budget.
- A slow scale-up no longer lets a burst of stale sample ticks pile up and evaluate as soon as it finishes — the sample window resets across a scale operation, and ticks are skipped while one is in flight.
- Scale-down is now opportunistic: it takes only whatever is already idle, instead of blocking the entire engine (and every other pending command) for up to `baseTimer` waiting for busy items to free up. A partial reduction now also advances the reported capacity correctly, matching the scale-up side.
- The heartbeat pump's own internal acquire no longer counts toward the autoscale fault budget — only genuine caller demand does.
- `RingBuffer<T>.New(string, ILoggerFactory)`'s `buffername` parameter is now correctly annotated as non-nullable, matching its existing `ArgumentNullException` behavior (was `string?`, misleadingly suggesting `null` was accepted).
- `ScaleDownMin` (calculated but never read by the autoscale decision) removed, along with its documented-but-unreachable "scale down from minimum capacity" formula — minimum capacity is the floor; there is nothing to scale down to below it.
- A heartbeat callback that blocks past its pulse budget no longer risks a use-after-dispose race on the resource it's still holding — the slot is still replaced promptly (as before), but the stuck resource itself is now only disposed once the orphaned callback actually finishes.
- A scale-up (or heartbeat-triggered replacement) that only partially completes now keeps trying the remaining items instead of abandoning them on the first item's failure/timeout, when `maxConsecutiveFactoryFailures` is opted into (see Added, above).
- A normal `DisposeAsync` racing an in-progress scale-up or replacement is no longer logged as a factory `TimeoutException` - only a genuine per-item/overall timeout is.
- A normal `DisposeAsync` racing a heartbeat callback that is still running no longer disposes the pooled resource out from under it - this now holds regardless of whether the callback is still inside its own pulse budget, closing a gap the previous fix (above) left open for the ordinary-shutdown case.
- A normal `DisposeAsync` racing an in-progress `WarmupAsync()` is no longer logged as `InvalidOperationException("RingBuffer did not reach initial capacity")` - only a genuine failure to reach capacity is.
- Autoscale-on-fault's scale-down evaluation no longer gets permanently stuck at an off-tier capacity left by a partial scale-up/down (e.g. `maxConsecutiveFactoryFailures` tolerating some failures during a scale-up) - it now re-evaluates from wherever the buffer actually is, using a safety margin scaled to that capacity instead of one fixed to the exact initial/maximum tier.

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
