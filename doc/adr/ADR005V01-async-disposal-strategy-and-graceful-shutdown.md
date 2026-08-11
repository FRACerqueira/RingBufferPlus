<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Async disposal strategy and graceful shutdown|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Async disposal strategy and graceful shutdown

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: `RingBufferManager<T>.Dispose()` (`src/RingBufferPlus/Core/RingBufferManager.cs`) runs a blocking synchronous `.Wait()` on several background tasks (logger, heartbeat, autoscale) and is registered as the cancellation callback of the very token it cancels (`_managertoken.Token.Register(() => Dispose())` inside `CleanupResources`, which calls `_managertoken.Cancel()`), creating reentrancy. In addition, `_blockScale` is never `Dispose()`d (only `_blockLogger` is), and the scale paths do not catch `ObjectDisposedException` when `_semaphoreBuffer` is disposed while a task is still blocked on it.

## Context and Problem Statement

Modern hosts (.NET Generic Host / ASP.NET Core) expect `IAsyncDisposable` to avoid blocking threads during graceful shutdown. Today, `IRingBufferService<T>` only exposes synchronous disposal via `IDisposable`, and the implementation has an unhandled cancellation reentrancy. How do we fix this without breaking existing consumers who already use `using var rb = ...`?

## Decision Drivers

* Existing consumers use `IDisposable`/`using` — removing that interface would be a breaking change (see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)).
* Modern async hosts prefer `DisposeAsync` for non-blocking shutdown.
* The current reentrancy (`Dispose()` called from within the cancellation callback of the very token it cancels) and the `_blockScale` leak are independent bugs from the interface choice, and must be fixed regardless.

## Considered Options

* Keep only synchronous `IDisposable`; fix only the reentrancy and the `_blockScale` disposal (minimal fix, no new API).
* Add `IAsyncDisposable` alongside `IDisposable` (additive, non-breaking), with `DisposeAsync` using `await Task.WhenAll(...)` instead of `.Wait()`, keeping synchronous `Dispose()` as a fallback.
* Make `RingBufferManager` fully async-first and remove `IDisposable`, leaving `IAsyncDisposable` as the sole disposal contract on `IRingBufferService<T>` and `RingBufferValue<T>`.

## Decision Outcome

**Revised on 2026-08-11 under the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate:** with breaking changes authorized for v5.0.0, the original justification ("additive so as not to break the API") no longer applies. The maintainer explicitly decided on the exclusive async-first option.

Chosen option (revised): "Async-first exclusive — remove `IDisposable`, `IAsyncDisposable` as the sole disposal contract" on `IRingBufferService<T>` and `RingBufferValue<T>`. This eliminates by design the duplicated cleanup logic between `Dispose`/`DisposeAsync` (the divergence risk cited in the original version of this ADR disappears because only one path exists), and the `Register(() => Dispose())` reentrancy itself stops being a problem to "fix" — the new `DisposeAsync` is written from scratch as part of the Channel-based rewrite ([ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)), without inheriting the problematic callback-registration pattern.

Fixes that remain valid, now as part of the single `DisposeAsync`'s design (no longer as a "patch" on top of the existing `Dispose`): (1) no reentrancy — cleanup does not register itself as a callback of the very token it cancels; (2) `await Task.WhenAll(...)` instead of a blocking synchronous `.Wait()` for background tasks; (3) `_blockScale` (or any equivalent queue in the new design) is always disposed; (4) scale paths correctly handle `ObjectDisposedException`, or, in the new Channel-based model, this risk is structurally eliminated by the single loop owning the state.

### Positive Consequences

* Eliminates by design the duplicated cleanup logic between two contracts — only one disposal path exists to maintain.
* Eliminates by design the `Dispose()` reentrancy (there is no longer a synchronous `Dispose()` to re-enter).
* Consistent with the modern .NET pattern for resources with potentially asynchronous cleanup (e.g. `DbContext`, `Channel`).
* Consumers on async hosts use `await using` with no thread blocking during shutdown.

### Negative Consequences

* Breaks every current consumer using synchronous `using var rb = ...` — including all 5 samples in this repository (`samples/*`), which need to be migrated to `await using` as part of v5 itself.
* Migration cost managed via `CHANGELOG.md`'s "Breaking changes v5.0.0" section — not free, a design decision consistent with the mandate, not an absence of trade-off; no dedicated migration guide is produced (see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)'s revision note).

## Pros and Cons of the Options

### Minimal fix, IDisposable only (no new API)

* Good, because it is the smallest possible change scope.
* Bad, because it does not solve the blocking-shutdown problem on async hosts, which was the original gap identified — discarded, the mandate removes the reason to pick the smaller-scope option.

### Add IAsyncDisposable additively (original option, superseded)

* Good, because it did not break any existing consumer.
* Bad, because it perpetuated duplicated cleanup logic between `Dispose`/`DisposeAsync` — exactly the kind of debt the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate authorizes eliminating at the root.

### Async-first exclusive, remove IDisposable (chosen)

* Good, because it eliminates duplication and reentrancy by design, not by maintenance discipline.
* Good, because it aligns with the modern .NET pattern for resources with async cleanup.
* Bad, because it is a real breaking change for every synchronous consumer — cost managed via the CHANGELOG's breaking-changes section (Phase 5.4), not avoided.

## Links

* Refined by: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — mandate that authorizes removing `IDisposable`.
* Related: [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — the new `DisposeAsync` is designed together with the Channel-based rewrite, not in isolation.
* Related: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — this removal of `IDisposable` is one of the changes covered by stage (a) — the single v5.0.0 reset.
