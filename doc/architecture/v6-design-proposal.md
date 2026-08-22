# RingBufferPlus v6.0.0 — Design Proposal (Consolidated)

This document consolidates a full design analysis conducted before any v6.0.0 implementation work started. It exists to carry the reasoning behind each decision into the ADRs that formalize them (see the mapping table at the end). Once those ADRs are accepted, this document is a narrative companion, not the source of truth — the ADRs are, following this project's own established convention (see ADR006's revision note on `doc/action-plan.md`).

## 1. Context: three generations, not two

- **`main` (v4.0.1, released)** — the currently-published stable line. Its engine (`RingBufferManager<T>`) mixes three overlapping concurrency primitives (`SemaphoreSlim`, a `lock`, two `BlockingCollection`s) guarding the same mutable state, with active-polling loops (`while` + `Task.Delay(2)`) and an `async void` disposal path.
- **`develop` (v5.0.0, released 2026-08-12)** — **this was verified against the real NuGet listing during this analysis**: v5.0.0 is published, marked "latest", 90 downloads, with 4.0.1 and earlier now deprecated (the same pattern this project applies to every superseded major). It is a genuine, coordinated rewrite authorized by ADR006 ("no commitment to the current version"): a single-consumer-owns-the-state engine over `System.Threading.Channels` (4 cooperating tasks, one authoritative state owner — not literally one thread, but one *owner*), `IAsyncDisposable` exclusive, a type-safe builder (`FixedCapacity`/`ElasticCapacity`), native OpenTelemetry observability, and 8 rounds of post-release hardening audits (121 findings, the last round still finding 2 High-severity issues).
- **v6.0.0 (proposed, this document)** — a further breaking overhaul, requested and analyzed in this conversation, reusing `develop`'s validated foundations but replacing its concurrency-ownership model, its autoscale algorithm, and several surface decisions.

**Correction carried from this analysis**: an earlier working assumption that v5.0.0 was never actually released ("ghost version") was wrong and was corrected mid-analysis via a live NuGet.org check. The v6.0.0 mandate (§2) is written against the real fact that v5.0.0 shipped to real (if few) consumers.

## 2. Mandate for v6.0.0

ADR006 authorized "no commitment to the current version" **for v5.0.0 specifically**, and explicitly rejected treating every future major as a free pass to break compatibility — that erodes the trust SemVer is meant to build.

v6.0.0 is justified not by reopening that rejected option, but on its own terms: v5.0.0 shipped, and v6.0.0 deliberately supersedes it, following the same pattern this project already applies to every prior major (deprecate the old one immediately, explain the break in the CHANGELOG). The honest, load-bearing difference from a routine major bump is that the turnaround is fast (~10 days) for a small but real user base (90 downloads) — this must be stated plainly in the v6.0.0 release communication, not minimized.

**Going forward**: strict SemVer resumes from v6.0.0 onward (not v5.0.0, since that promise's target release changed). No further "free" breaking majors without their own justification — this ADR does not become a standing precedent.

## 3. Concurrency model: one owner of truth, three pure executors

`develop`'s ADR001V02 already established the principle that matters: a single owner mutates all capacity state; satellites only send commands. v6 generalizes this to more satellites without violating that principle — it does **not** move to a "three independent actors" model (that would reopen the exact class of bug ADR001 was written to close, and would forfeit `develop`'s 8 rounds / 121 findings of hardening for no reason).

- **Orquestrador** — the single owner. Owns `Channel<T>`, handles acquire/return/invalidate, tracks available / in-flight-creating / in-flight-removing, derives `CurrentCapacity` and the capacity-state properties. Only it ever increments or decrements the authoritative counters, and only on confirmed completion from a satellite — never on request.
- **Fábrica** — executes "create N" commands from the Orquestrador. Runs the user factory with its own per-call timeout (`FactoryTimeout`), applies a simple growing backoff after consecutive failures (self-protection against hammering a broken factory — not a circuit-breaker state machine), reports completed count and last fault. Concurrency is bounded by a small new parameter (`MaxConcurrentFactoryCalls`) — this is what prevents a large backlog spike from flooding a struggling downstream with simultaneous connection attempts (see §5).
- **Remoção** — executes "dispose these N items" commands (the Orquestrador dequeues them from the channel first — Remoção never touches the channel directly). Isolated because disposal can block (a real network round-trip for a DB/AMQP connection), same reasoning that justified isolating Fábrica.
- **Monitor** — the slow/predictive layer (§4). Purely computational: samples a signal, computes a target, reports it. Never mutates state directly.

## 4. Autoscale algorithm: percentile + regression, replacing the median (backed by evidence, not just judgment)

ADR003 originally kept the median-of-samples algorithm "for lack of evidence a change would help," and ADR006 explicitly said this should only be revisited with data. This analysis produced that data: a standalone decision-quality simulation (`benchmarks/RingBufferPlus.Benchmarks/AutoScaleAlgorithmComparison.cs`, run via `dotnet run -c Release -- --algo-comparison`), which ports the real `AutoScaleDecision` median logic verbatim and compares it against the proposed percentile+regression Monitor across four synthetic demand traces.

**Findings**:
- The real median algorithm can get **structurally stuck** at a coarse tier for a long time — in the simulated scenarios, it never fully descended after a demand drop (stuck at the intermediate tier), and stayed pinned at `MaxCapacity` for the rest of two other scenarios, because its scale-down formula uses a strict `>` comparison that a specific-but-plausible demand level can permanently fail to clear.
- The percentile+regression Monitor converges far faster and right-sizes tighter in every scenario, but its raw form oscillates heavily (48 direction changes in 300 ticks) under flat-but-noisy demand — confirming, with a real number, a theoretical concern raised during design review.
- Adding a **deadband** (don't move the target for changes smaller than the buffer's own noise tolerance) dropped oscillations from 48 to 1 with no measurable cost elsewhere.
- Adding a **window reset while a reactive episode is in flight** (mirroring a real, already-shipped `develop` behavior described in its own CHANGELOG: "sample window resets across a scale operation, ticks are skipped while one is in flight") dropped post-burst convergence from 18 ticks to 1 in both affected scenarios, with lower average over-provisioning in every scenario and no oscillation regression.

**Decision**: replace the median algorithm with sliding-window percentile (p95 default) + safety buffer (10% default) as the "fair level", adjusted by a linear-regression trend projected a configurable horizon ahead, clamped to `[min, max]`, with the deadband and reactive-window-reset refinements above as integral parts of the design (not optional extras).

## 5. Reactive path: backlog depth, not fault counting

The shipped `AutoScaleReactionBenchmarks` (run against `develop` during this analysis: `ReactToAcquireFault_ScalesFromInitToMax`, mean 77.46 ms) confirms the current reactive trigger is dominated by `AcquireTimeout` (≈65% of total reaction time is just waiting for the configured timeout to elapse) rather than by the actual cost of scaling.

**Decision**: the reactive path triggers on real-time backlog depth (callers currently waiting) rather than counting acquire faults — it reacts before any timeout elapses, and proportionally to the actual gap (`waiting − available`), not a coarse tier jump.

**Thundering-herd mitigation** (a downstream that is slow-but-technically-healthy, not actually short of capacity, must not get *more* concurrent connections thrown at it): the Orquestrador always nets out already-in-flight creation before issuing a new request (no duplicate requests across overlapping backlog waves), and Fábrica's bounded concurrency (§3) throttles how fast new instances can ever be created — if the downstream really is unhealthy, the throttled creation attempts themselves start failing and trip the existing backoff, closing the loop without a circuit-breaker. Lease duration is exposed as an observability metric (not acted on automatically) so an operator can distinguish "pool undersized" from "downstream slow."

## 6. Capacity model and the floor guard

- `min`, `max`, optional `target` (defaults to `min` in elastic mode) — explicit, named, validated at configuration time. `min == max` means no elasticity; `target`, if given, must equal both, else a configuration error.
- **Floor guard**: `available ≤ min` triggers an immediate, undebounced replenishment request — highest priority of all signals. The public "below minimum" flag only becomes true if that replenishment fails to restore `min` within one `FactoryTimeout` cycle (reusing the existing parameter as the grace window, adding no new one) — a real blip self-heals invisibly; a genuinely broken factory is reported truthfully, just not instantly on the first touch of the floor.
- Signal priority inside the Orquestrador: **floor guard > backlog-reactive > manual pin (while active) > Monitor (predictive)**.

## 7. Manual scale: a temporary pin, not a competing mode

v4/`develop` treated automatic and manual scaling as mutually exclusive modes (silently in v4, a compile-time exclusion in `develop`'s ADR007V02). v6 has no such mode switch — the three automatic signals are always active for an elastic pool, so `SwitchToAsync` is redefined as a **temporary pin**: it substitutes for the Monitor's predictive output for an explicit, mandatory duration (no default — this project has twice shipped a silent, permanent behavioral trap from an unbounded scaling override; a mandatory duration prevents a third instance). The floor guard and backlog-reactive signals are never suppressed by a pin — real waiting callers and the safety floor are always honored regardless of what an operator pinned earlier. `LockWhenScaling` stays removed (already decided in `develop`'s ADR007V02 amendment; the async, command-queue-based Orquestrador model has no blocking-caller concept to reintroduce).

## 8. Remaining public surface

- **HeartBeat** — redesigned from `Action<RingBufferValue<T>>` (with a "do not dispose this yourself" documented trap) to `Func<T, bool>`: the framework acquires/returns internally and interprets `false` as the same `Invalidate()` path any consumer uses — one mechanism, not two, and no disposable object handed to code that must not dispose it.
- **BackgroundLogger** — removed. It re-implements, with a shared cross-thread queue, something the standard `ILogger` ecosystem already solves generically (async-capable providers); it was also structurally the same class of shared-queue risk ADR001 flagged as a real bug source in `main`.
- **OnError** — kept, simplified to `Action<Exception>` (the logger is already configured separately), called inline, no queue.
- **HostingExtensions** — `WarmupRingBufferAsync` (which ADR006 already flagged for an ignored `token` and a null-check that can never fire) is replaced by a proper `IHostedService` that calls `BuildWarmupAsync` with its own `StartAsync(CancellationToken)` token — fixes both known bugs at the root via the idiomatic .NET hosting contract, instead of patching the existing bespoke extension.
- **`AcquireDelayAttempts`** — removed; it configured a manual polling loop's delay that no longer exists once acquire is a `Channel<T>` read with a linked timeout token (already true in `develop`, just not yet reflected in the parameter list).
- **`RingBufferValue<T>` / `Invalidate`** — stays `IAsyncDisposable` exclusive (ADR005, reused). Disposing an invalidated lease is a fast, non-blocking notification to the Orquestrador, which hands the actual disposal to the Remoção actor — the caller's `DisposeAsync()` never blocks on real I/O.

## 9. What this reuses from `develop` unchanged

`Channel<T>` as the underlying store; `IAsyncDisposable` exclusive disposal (ADR005); the type-safe builder pattern distinguishing fixed vs. elastic modes (ADR007, extended with `target`); partial-capacity-gain semantics on an incomplete scale-up (ADR010); warmup retry semantics (ADR011); native `Meter`/`ActivitySource` observability (ADR008) as the channel for the Monitor's metrics and the lease-duration diagnostic.

## 10. Mapping to ADRs

| Decision area | ADR action |
|---|---|
| v6.0.0 mandate, given a real v5.0.0 release | New ADR, explicitly superseding ADR006's version-scoped promise |
| Concurrency model (Orquestrador/Fábrica/Remoção/Monitor, floor guard, bounded factory concurrency) | Revise ADR001 (new version) |
| Autoscale algorithm (percentile+regression+deadband+window-reset) | Revise ADR003 (new version), backed by `AutoScaleAlgorithmComparison` |
| Reactive path (backlog depth, not fault count) | Part of the ADR001 revision |
| Capacity model (`target`), manual pin, HeartBeat/Logger/OnError/HostingExtensions/`AcquireDelayAttempts` | Revise ADR007 (new version) |
