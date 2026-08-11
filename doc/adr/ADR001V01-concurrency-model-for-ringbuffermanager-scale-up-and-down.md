<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Concurrency model for RingBufferManager scale up and down|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Concurrency model for RingBufferManager scale up/down

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: Architecture review (PO + senior architect) identified 4 active concurrency defects in `RingBufferManager<T>` (`src/RingBufferPlus/Core/RingBufferManager.cs`).

## Context and Problem Statement

`RingBufferManager<T>` coordinates elastic capacity using three overlapping, simultaneous concurrency primitives: `SemaphoreSlim(1,1)` (`_semaphoreBuffer`) to serialize warmup/scale-up/scale-down; a `lock`/`System.Threading.Lock` (`_lock`) to guard enqueueing into `_blockScale`; and two `BlockingCollection<T>` (`_blockScale`, `_blockLogger`) as producer/consumer queues for lock-free scaling and background logging. This combination has already produced real, code-verified bugs:

1. `_semaphoreBuffer.Release()` in the `finally` of `ScaleUpProcessAsync`/`ScaleDownProcessAsync` runs even when the corresponding `WaitAsync` was cancelled (never acquired the lock) → an uncaught `SemaphoreFullException` that silently kills the autoscale task for the rest of the buffer's lifetime.
2. `Startup()` checks `_WarmupDone || _WarmupRunning` outside any lock (check-then-act) — two concurrent `AcquireAsync` calls on a cold buffer can trigger duplicate warmup, creating duplicate logger/heartbeat/autoscale threads and a spurious `InvalidOperationException`.
3. Reads of `_autoscaleRunning` outside `_lock` (in `AcquireAsync`/`SwitchToAsync`) allow conflicting scale requests to be queued.
4. `Dispose()` is registered as the cancellation callback of the very token it cancels, and disposes `_semaphoreBuffer` while scale tasks may be blocked on it — a path with no `ObjectDisposedException` handling (unlike `LogMessage`/`LogError`, which already catch it).

How do we fix this without compromising the product's central pitch ("lock-free scaling on acquire")?

## Decision Drivers

* Fix correctness bugs with minimal regression risk, without breaking the public API (breaking-change cost is high — see [[ADR004]]).
* Preserve the "background scaling without blocking `AcquireAsync`/`SwitchToAsync`" philosophy (the `LockWhenScaling` command already documents this trade-off for whoever wants the opposite).
* Avoid a full rewrite of the core without concurrency-test coverage, which does not exist today (see Action Plan, Phase 3).
* Reduce, in the medium term, the number of distinct primitives guarding the same state (`_currentCapacity`, `_autoscaleRunning`).

## Considered Options

* Fix the 4 point bugs while keeping the 3 current primitives (semaphore + lock + BlockingCollection).
* Rewrite `RingBufferManager` as a single state machine built on `System.Threading.Channels`, with a single consumer loop owning all mutation of `_currentCapacity`/`_autoscaleRunning` (no explicit lock, just channel ordering).
* Introduce an internal layer ("ScaleCoordinator") that wraps all state mutation behind a single primitive, without removing the existing `BlockingCollection`s.

## Decision Outcome

**Revised on 2026-08-11 under the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate:** the maintainer authorized sweeping breaking changes for v5.0.0, removing the "do not break `IRingBufferService<T>`" constraint that motivated the original option. Decision revised.

Chosen option (revised): "Rewrite `RingBufferManager` as a single state machine built on `System.Threading.Channels`", instead of a surgical fix to the 3 current primitives. With breaking changes authorized, fixing the structural root cause (multiple primitives guarding the same state) in v5 is preferable to fixing 4 point bugs in a design that would remain prone to the same class of error.

**Mandatory sequencing (non-negotiable):** the rewrite can only start once the behavioral contract tests (Action Plan, Phase 1) are written against the system's *intended* behavior (not against the current implementation, where the bugs live). Those tests are the rewrite's acceptance criterion — without them, rewriting the concurrency core is exactly the risk that the original version of this ADR used to justify not rewriting now. This is reflected in the revised Phase 1 of `action-plan.md`.

### Positive Consequences

* Resolves the structural root cause (multiple primitives guarding the same state) instead of only treating symptoms.
* Eliminates by design the "check-then-act outside lock" bug class and the `Dispose()` reentrancy (see [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)).
* Simplifies reasoning about `_currentCapacity`/`_autoscaleRunning` (a single thread/loop owns the state).
* Opportunity to also fix the `WarmupRingBufferAsync` signature (ignored token, `ArgumentNullException` that never fires) in the same rewrite cycle.

### Negative Consequences

* High-risk rewrite without today's concurrency test safety net — mitigated by the mandatory sequencing above (contract tests before code).
* Breaks `IRingBufferService<T>`/`IDisposable` for current consumers — cost managed via the v4→v5 migration guide (Action Plan, Phase 6).
* May introduce new, subtle ordering bugs in the new model if test coverage is not equivalent to or better than the current model's.

## Pros and Cons of the Options

### Fix point bugs (keeping the current model)

Surgical fix to the 4 points identified in `RingBufferManager.cs`.

* Good, because it is low risk and does not break the public API.
* Good, because it can be validated with unit/concurrency tests targeted per bug.
* Bad, because it does not resolve the structural root cause (multiple primitives guarding the same state).

### Single state machine via Channel<T>

Replace semaphore + lock + BlockingCollection with a single channel-consumer loop.

* Good, because it eliminates by design the "check-then-act outside lock" bug class.
* Good, because it simplifies reasoning about `_currentCapacity`/`_autoscaleRunning` (a single thread owns the state).
* Bad, because it is a high-risk rewrite without today's concurrency test safety net.
* Bad, because it may introduce new, subtle ordering bugs without equivalent coverage.

### Internal ScaleCoordinator (partial encapsulation)

Middle ground: a single lock guard around all state reads/writes, without removing the queues.

* Good, because it reduces the "multiple guardians" surface without rewriting the queue pipeline.
* Bad, because it still mixes paradigms (queue + lock) and does not resolve the `async void`/Dispose reentrancy issue.

## Links

* Related: [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) — the async disposal strategy addresses the `Dispose()` reentrancy mentioned in context item 4.
* Related: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — the breaking-change policy limits how freely this ADR can alter public signatures.
