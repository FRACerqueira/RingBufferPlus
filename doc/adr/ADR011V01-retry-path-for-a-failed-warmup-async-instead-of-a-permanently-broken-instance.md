<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Retry path for a failed WarmupAsync instead of a permanently broken instance|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-20)|
|Changed|Accepted (2026-08-20)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Retry path for a failed WarmupAsync instead of a permanently broken instance

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: v5.0.0 product-viability audit (`TODO/relatorio-viabilidade-ringbufferplus-v5.md`), finding U-22 / §4.2 decision B.

## Context and Problem Statement

`RingBufferManager<T>` wraps its warmup attempt in a `Lazy<Task>` (`ExecutionAndPublication`), which caches the outcome — including a failure — forever. Once `WarmupAsync()`/`BuildWarmupAsync()` throws, every subsequent call on that instance rethrows the exact same cached exception; the only way to recover is constructing an entirely new instance. Nothing in the public XML documentation states this, and both `doc/guides/usage-dependency-injection.md` and `concepts.md` recommend registering the buffer as a DI singleton — meaning a single transient failure at host startup (a database not yet accepting connections, a broker still booting) disables the pool for the remaining lifetime of the process, recoverable only by a full process restart. Is this permanence acceptable given how the library is actually recommended to be used, or should a failed warmup be retriable?

## Decision Drivers

* The library's own onboarding guidance (DI guide, `concepts.md`) pushes exactly the usage pattern — a long-lived singleton — that this permanence hurts most: one bad moment at startup costs the pool for the process's entire lifetime.
* A transient factory failure at startup (a dependency not yet ready) is an ordinary, expected occurrence for anything pooling external resources — not an exceptional case worth permanently disabling the pool over.
* `AcquireAsync`/`SwitchToAsync` must not be allowed to silently trigger retries of their own — an implicit retry on every acquire against a still-broken factory would turn ordinary request traffic into a retry storm against an already-struggling dependency.
* `Lazy<T>`'s own caching is exactly why v4's retry path was broken in a different way (`_WarmupRunning` stuck permanently true) - whatever replaces it must not reintroduce a comparable stuck-state bug, and must stay compatible with `Lazy<Task>`'s existing role of de-duplicating concurrent callers into one attempt (ADR001).

## Considered Options

* Keep the current behavior, but document it clearly on `WarmupAsync`/`BuildWarmupAsync` and in the DI guide, leaving retry as the operator's own responsibility (rebuild the instance, or restart the process).
* Add an explicit retry path: a `WarmupAsync()` call made after a previous failure installs a fresh attempt and retries, instead of rethrowing the cached failure forever.
* Make retries automatic and implicit everywhere warmup is observed (including through `AcquireAsync`'s own trigger), optionally with backoff.

## Decision Outcome

Chosen option: "Add an explicit retry path." A call to `WarmupAsync()` after a previously faulted attempt now installs a fresh `Lazy<Task>` (via `Interlocked.CompareExchange`, racing multiple concurrent callers safely onto a single winner) and retries from scratch, instead of rethrowing the same cached exception forever. The fully-implicit/automatic option was rejected specifically for `AcquireAsync`/`SwitchToAsync`'s benefit: their own warmup trigger (`EnsureWarmupAsync`) deliberately does **not** retry on its own - it only observes whatever the most recent attempt's outcome is. Retrying is always the result of a deliberate `WarmupAsync()` call, never a side effect of ordinary acquire traffic. The "document only" option was rejected because it leaves the actual pain (a singleton, permanently dead after one transient blip) unresolved - documenting a bad default is not the same as fixing it, and the fix here carries no compatibility cost: any code that already retries by discarding the whole instance and rebuilding it keeps working exactly as before, and now has a cheaper option available too.

### Positive Consequences

* A transient factory failure at startup no longer permanently disables a DI-registered singleton - calling `WarmupAsync()` again (e.g., from a startup retry loop, or a health-check-triggered re-warmup) can recover it.
* No change to `AcquireAsync`/`SwitchToAsync`'s own failure behavior - they still surface whatever the latest warmup attempt's outcome is, exactly as before, so this is purely additive from their perspective.
* The background logger pump is now guarded against being started twice across retries (a bug that would otherwise appear the first time this path was actually exercised) - closed in the same change.

### Negative Consequences

* A consumer that only ever calls `AcquireAsync`/`SwitchToAsync` (never `WarmupAsync()` explicitly) still sees the old permanent-failure behavior in practice, since nothing on that path triggers a retry - this decision fixes the case the DI guide actually recommends (explicit `WarmupAsync()`/`BuildWarmupAsync()` at startup), not every possible usage pattern.
* One more piece of lock-free state management (`Interlocked.CompareExchange` on `_warmup`) in a class whose whole architectural pitch (ADR001) is "a single consumer owns all mutable state" - this is a deliberate, narrow exception: warmup identity/retry bookkeeping is orthogonal to the scale-state the engine loop owns, and the CAS pattern is self-contained (readable in one method) rather than spreading further lock-free coordination through the class.

## Pros and Cons of the Options

### Document the permanence, no code change

* Good, because it costs nothing to implement and carries zero regression risk.
* Bad, because it leaves the actual operational pain unresolved for exactly the usage pattern (DI singleton) the library's own guides recommend.

### Explicit retry via WarmupAsync() (chosen)

* Good, because it directly fixes the scenario the audit flagged, with no compatibility cost for existing callers.
* Good, because keeping the retry trigger explicit (not automatic on `AcquireAsync`) avoids turning ordinary traffic into a retry storm against a struggling dependency.
* Bad, because a consumer relying only on `AcquireAsync`'s implicit warmup trigger sees no benefit unless they also call `WarmupAsync()` themselves after a failure.

### Fully automatic/implicit retry everywhere (rejected)

* Good, because it would help even consumers who never call `WarmupAsync()` explicitly.
* Bad, because an acquire-triggered retry against a factory that is still down turns every subsequent `AcquireAsync` call into a fresh, uncoordinated retry attempt - exactly the load-amplification problem this audit already found and fixed elsewhere (R5's discarded-and-repeated scale-up channels).

## Links

* Related: [ADR001 V02](./ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) - `Lazy<Task>`'s role in de-duplicating concurrent warmup callers, which this decision preserves and extends rather than replaces.
* Related: `TODO/relatorio-viabilidade-ringbufferplus-v5.md`, finding U-22, and `TODO/plano-de-acao.md` P2 Decision B - the audit finding and implementation record for this decision.
