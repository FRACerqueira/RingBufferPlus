# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/) — with two documented exceptions: **v5.0.0 is a deliberate, single "clean slate" reset** with no deprecation bridge from v4.x, authorized by [ADR006](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md); and **v5.1.0 carries one narrow breaking removal without a version-major bump**, authorized by [ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md). Strict SemVer with a deprecation cycle otherwise applies from v5.0.0 onward (see [ADR004](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)).

## [Unreleased] — targeting 6.0.0

A complete, coordinated product overhaul authorized by [ADR006 V02](doc/adr/ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) - strict SemVer resumes from v6.0.0 onward (superseding the v5.0.0 exception recorded in [ADR006 V01](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)/[ADR004 V02](doc/adr/ADR004V02-semantic-versioning-policy-and-fluent-api-stability.md)); this is not a standing precedent for future majors. This section is being built up incrementally as work lands on the `v6` branch, not backfilled commit-by-commit for everything already done - the summary below covers what preceded the first per-item entry.

**Summary of what already landed before per-item entries started:** the concurrency model was redesigned around four cooperating roles - Orquestrador (sole owner of capacity state), Fábrica (bounded-concurrent, backgrounded item creation), Remoção (backgrounded item disposal on scale-down), and Monitor (a pure percentile+regression predictive algorithm, replacing the old median-based one) - see [ADR001 V03](doc/adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)/[ADR003 V03](doc/adr/ADR003V03-median-sample-autoscaling-algorithm.md). The old fault-count autoscale trigger was replaced by a backlog-reactive signal (real waiting-caller depth). `AutoScaleAcquireFault`/`IRingBufferAutoScaleBuilder<T>` were removed entirely: the floor guard, backlog-reactive signal, and Monitor are now unconditionally active for every elastic pool, and `SwitchToAsync` became a temporary pin (mandatory `pinDuration`, substitutes for the Monitor's own output, never suppresses the floor guard or backlog-reactive signal) instead of a mutually exclusive mode - see [ADR007 V03](doc/adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md).

### Breaking changes

- **`BackgroundLogger` removed entirely** (`IRingBufferBuilder<T>`/`IRingBufferFixedBuilder<T>`/`IRingBufferElasticBuilder<T>`): logging is now always synchronous, inline, on whichever thread triggered it - no dedicated background pump, no internal queue. It duplicated a problem the `ILogger` ecosystem already solves generically (async-capable providers), and was itself a repeat source of real bugs (a message-ordering/drop bug, and a throwing `OnError` permanently faulting the pump and silently dropping every later message) - see [ADR007 V03](doc/adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md).
- **`OnError` simplified**: from `Action<ILogger?, Exception>` to `Action<Exception>` - the logger is already configured separately via `Logger(ILogger?)`, so it is no longer also handed to the error callback.
- **`WarmupRingBufferAsync` removed**: `AddRingBuffer<T>` now also registers an `IHostedService` for the given `buffername`, which calls `WarmupAsync` automatically during the host's own startup (`StartAsync`, using that call's own token) - warmup is no longer a separate opt-in step. This fixes both root-level bugs the old extension had (an ignored token on one path, a dead null-check) by construction, via the idiomatic hosting contract, rather than patching the old signature. There is no way to opt out of automatic warmup via `AddRingBuffer<T>` itself; build the buffer directly via `RingBuffer<T>.New(...)` instead if you need that.
- **`HeartBeat` redesigned**: from `Action<RingBufferValue<T>>` to `Func<T, bool>`. The framework now owns acquiring and returning the heartbeat item; the callback receives the raw value and returns `true` to keep it or `false` to discard it (a replacement is created in its place, through the same path a caller's own `Invalidate()` uses) - there is no longer a disposable object handed to the callback to misuse (the previous "do not dispose this yourself" documented trap is gone by construction). A callback that blocks past its own `pulse` budget is still treated as a timeout regardless of what it would have returned; a thrown exception does not discard the item (same as returning `true`) - only an explicit `false` does.
- **`ElasticCapacity`'s parameters reshaped**: `minCapacity`/`maxCapacity` are now the first two (required) parameters, and the former `initialCapacity` is now a `target` parameter - the third, and now **optional** (defaults to `minCapacity`, "provision for demand, not worst case" - start small and let the floor guard/backlog-reactive signal/Monitor grow it, rather than an arbitrary middle default). `minCapacity == maxCapacity` is a valid, degenerate configuration (no real elasticity) rather than an error; an explicitly-given `target` must still fall within `[minCapacity, maxCapacity]` (so it must equal both in that case), enforced by the same validation `Build`/`BuildWarmupAsync` already ran for `initialCapacity`. Every positional call site changes shape: `ElasticCapacity(initialCapacity, minCapacity, maxCapacity, ...)` becomes `ElasticCapacity(minCapacity, maxCapacity, target, ...)`.

### Fixed

- A builder configured with `OnError(...)` but no `Logger(...)` now actually has its error handler invoked during `Build`/`BuildWarmupAsync` validation failures - previously the guard required a non-null, Error-enabled `Logger` before even checking whether an `OnError` handler existed, silently skipping it otherwise.

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
- `DisposeAsync` now actually waits (bounded by `PulseHeartBeat`) for an orphaned `HeartBeat` callback's deferred resource disposal to finish before returning, instead of merely scheduling it - closing a real resource leak if the host process exited shortly after `DisposeAsync()` returned.
- A heartbeat tick's own internal acquire no longer surfaces an unhandled `ObjectDisposedException` (logged as an unexpected error) when it narrowly races an ordinary `DisposeAsync()`.
- A normal `DisposeAsync` racing a heartbeat callback that finishes at essentially the same instant no longer logs the resulting cancellation as an error - closing a residual gap in the previous heartbeat-dispose-race fixes (above) for this specific timing.
- Scale-down is no longer unreachable (from above initial capacity) or effectively unreachable (from at-or-below initial capacity, requiring exactly zero acquisitions) when `initialCapacity` or `minCapacity` is 2, the minimum legal value - the scale-down safety margin is now capped so it can never require more idleness than the buffer's own current capacity allows.
- `scale.operations`/`scale.duration` and the `RingBufferPlus.Scale` activity now carry a `cancelled` tag, and no longer report a scale operation cancelled by an ordinary `DisposeAsync()` identically to a genuine factory failure (its `ActivityStatusCode` is `Ok`, not `Error`, when cancelled by shutdown).
- The `RingBufferPlus.Acquire` activity now sets an `ActivityStatusCode` on every outcome (previously never set on any path) - `Error` only for a genuine `AcquireTimeout`, `Ok` for success, a caller cancellation, or an ordinary shutdown.
- `DisposeAsync()`'s idle-item drain loop now disposes every remaining pooled item even if one of them throws on `Dispose()`, instead of aborting on the first failure and leaking the rest (plus the buffer's own `Meter`/`ActivitySource`).
- With `BackgroundLogger(true)`, `DisposeAsync()` no longer silently drops its own later log messages (a background-pump failure, the heartbeat-disposal grace-period warning, or the item-drain loop's own defensive logging) by completing the log queue too early.
- The internal bag tracking deferred heartbeat-callback resource disposals no longer grows unboundedly under a chronically slow `HeartBeat` callback - already-finished entries are pruned as new ones are added.
- `acquire.duration` and the `RingBufferPlus.Acquire` activity now carry `acquire.timed_out`/`acquire.cancelled` tags on a failed outcome, distinguishing a genuine `AcquireTimeout` from an ordinary caller-cancellation or shutdown.
- `scale.operations`/`scale.duration` and the `RingBufferPlus.Scale` activity no longer report a scale-up as an ordinary cancelled shutdown when a genuine factory failure occurred earlier in the same batch and a later attempt was then cancelled by `DisposeAsync()`.
- The grace-period-timeout message logged when `DisposeAsync()` cannot wait for an orphaned heartbeat callback's deferred disposal is now a `LogWarning`, not an informational message - it is a real, indeterminate-duration resource leak, not an ordinary shutdown-vs-failure ambiguity.
- A throwing `Logger`/`OnError` sink can no longer permanently kill the background logger pump, permanently lose a pool slot and make `DisposeAsync()` itself throw via the heartbeat-timeout path, or otherwise escape into unrelated core operations - every invocation of the user-supplied sink is now defensively guarded.
- `scale.operations`/`scale.duration` and the `RingBufferPlus.Scale` activity no longer report a scale-up as an ordinary cancelled shutdown when the batch made zero progress due to a genuine factory failure and a later attempt was then cancelled by `DisposeAsync()` - closing a residual gap in the previous fix (above) for this specific batch outcome.
- Invalidating an acquired item (`Invalidate()`) and disposing it no longer permanently loses that pool slot if the item's own `Dispose()`/`DisposeAsync()` throws - the replacement is now always queued regardless, with the item's own exception still propagating to the caller unchanged.
- A throwing `OnError` handler encountered while `Build()`/`BuildWarmupAsync()` is reporting a real validation failure no longer replaces that failure with the handler's own exception.
- `scale.operations`/`scale.duration` and the `RingBufferPlus.Scale` activity no longer report a normal, partial scale-down (not enough idle items available right now, by design) as `ActivityStatusCode.Error` - a scale-down can never genuinely fail or be cancelled, so it is now always reported as `Ok` regardless of whether it fully reached its target (`success` still reflects that).
- A pooled item's own `Dispose()`/`DisposeAsync()` is now bounded by `PulseHeartBeat` wherever the library waits on it (`DisposeAsync()`'s idle-item drain loop, and scale-down's item removal) - a slow or hanging item dispose can no longer block shutdown indefinitely, nor stall the engine's single-consumer loop (and therefore every other pending command) during a scale-down.
- `WarmupAsync()`/`BuildWarmupAsync()`, `SwitchToAsync()`, and the heartbeat-triggered item replacement no longer misclassify a genuine `OperationCanceledException`/`TaskCanceledException` thrown by the factory itself (e.g. an `HttpClient`/gRPC/DB driver's own unrelated internal timeout) as an ordinary shutdown of this buffer's own lifetime - `WarmupAsync()`/`BuildWarmupAsync()` now surface the real exception instead of a generic wrapper, `SwitchToAsync()` now surfaces it instead of returning `false` silently, and the replacement path now logs the real exception instead of a fabricated `TimeoutException`.
- A pooled item's own synchronous `Dispose()` (as opposed to `DisposeAsync()`) is now actually bounded by `PulseHeartBeat` too - it previously blocked inline before the existing grace-period wait could ever engage. Disposing a whole batch of removed/drained items (during `DisposeAsync()`'s shutdown drain or a scale-down) is also now bounded by roughly one `PulseHeartBeat` total instead of one per item.
- With `BackgroundLogger(true)`, a message logged after the internal background log channel has already been completed (e.g. a background dispose fault reported after `DisposeAsync()` already finished) is now still delivered synchronously instead of being silently dropped.
- A factory call that exceeds its own per-item `FactoryTimeout` (during a scale-up or a heartbeat-triggered item replacement) is now actually cancelled via the token it receives, instead of continuing to run orphaned in the background - a factory that honors `CancellationToken` (as most I/O-bound factories do) no longer keeps working, and eventually discarding without disposal, past the point this library already gave up waiting on it. A factory that does not honor the token at all is unaffected by this fix and remains a documented caller responsibility (see the XML doc on `Factory`'s `value` parameter).
- `CurrentCapacity` no longer silently over-reports the pool's real size forever when a heartbeat-triggered or `Invalidate()`-triggered item replacement fails to produce a new item - the removed slot is now correctly reflected in `CurrentCapacity` immediately. This closes a symptom of a 2026-08-20 finding (a factory failure during item replacement) that had been marked fixed at the time but was never actually corrected for this specific code path.
- `SwitchToAsync()`'s unlocked path (`LockWhenScaling` disabled, the default) no longer risks surfacing an unobserved task exception when the engine later resolves a genuine scale failure after the caller has already stopped waiting on it.

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
