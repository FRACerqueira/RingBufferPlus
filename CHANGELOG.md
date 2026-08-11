# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/) — with one documented exception: **v5.0.0 is a deliberate, single "clean slate" reset** with no deprecation bridge from v4.x, authorized by [ADR006](doc/adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md). Strict SemVer with a deprecation cycle resumes from v5.0.0 onward (see [ADR004](doc/adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)).

## [Unreleased]

v5.0.0 is a complete, coordinated product overhaul with sweeping breaking changes — see [doc/action-plan.md](doc/action-plan.md) and the ADRs in [doc/adr](doc/adr) for full context. Planned breaking changes:

- Concurrency core rewritten on `System.Threading.Channels` as a single state machine ([ADR001](doc/adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)). All `RingBufferManager<T>` mutable scale state is now owned exclusively by one consumer loop — no lock/semaphore is used or needed.
- `IDisposable` removed; `IAsyncDisposable` becomes the sole disposal contract on `IRingBufferService<T>` and `RingBufferValue<T>` ([ADR005](doc/adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)). `using`/`Dispose()` call sites must become `await using`/`DisposeAsync()`.
- **Breaking:** disposal is no longer triggered automatically when the constructor's lifetime `CancellationToken` is cancelled (the v4 `Register(() => Dispose())` reentrancy hazard is removed entirely). Code that relied on `cts.Cancel()` alone to tear a buffer down (outside of DI, where the `ServiceProvider` already calls `DisposeAsync` on `IAsyncDisposable` singletons) must call `DisposeAsync()` explicitly.
- Public fluent builder surface redesigned around explicit, mutually exclusive `FixedCapacity`/`ElasticCapacity` modes, replacing `Capacity`/`ScaleTimer`/`MinCapacity`/`MaxCapacity` ([ADR007](doc/adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)). `AutoScaleAcquireFault(...)` now returns a builder whose `Build()`/`BuildWarmupAsync()` produce a plain `IRingBufferService<T>` with no `SwitchToAsync` — manual switching and autoscale-on-fault are mutually exclusive at the type level, not a silent runtime `false`.
- **Breaking:** `AcquireTimeout`'s `delayAttempts` parameter and `RingBufferDefault.AcquireDelayAttempts` are removed — a `Channel`-based acquire has no polling loop to pace.
- **Behavior change:** acquire no longer blocks while a scale operation is in progress, regardless of `LockWhenScaling` — items already in the pool are always immediately acquirable. `LockWhenScaling` now controls exactly one thing: whether `SwitchToAsync`'s caller awaits the scale operation's completion before returning.
- **Behavior change:** autoscale-on-fault counts *timed-out acquires*, not per-poll-attempts-within-one-acquire-call as in v4. Where `AcquireTimeout` is large relative to `AutoScaleAcquireFault`'s threshold, scale-up now reacts on the order of `AcquireTimeout × numberOfFaults`, not near-instantly — tune `AcquireTimeout` down if fast autoscale reaction is required.
- `WarmupRingBufferAsync` signature fixed (the `token` parameter is now honored; missing-buffer now throws `ArgumentNullException` as documented, instead of silently no-oping).
- **v4.x and earlier receive no further fixes once v5.0.0 ships** — including the concurrency bugs listed under v4.0.1 below. See [ADR004](doc/adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) and `SECURITY.md`.

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
