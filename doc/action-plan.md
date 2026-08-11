# Action Plan — RingBufferPlus

Consolidation of the architecture review (PO + senior architect) into executable phases. Each item references file/line where applicable and, when the change involves a design decision, points to the corresponding ADR in [`doc/adr`](./adr).

**Strategy revision (2026-08-11):** the maintainer authorized sweeping breaking changes for the next major (**v5.0.0**) — a complete product overhaul, not an incremental accumulation of additive patches. This is formalized in [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) and changed the shape of Phases 1, 3, 5 and 6 below relative to the earlier version of this plan (which assumed "fix without breaking the API").

Decisions confirmed by the maintainer:
- **v5.0.0 has no commitment to v4.x** — the criterion is improving the product, even at the cost of total incompatibility. This applies only to the v5.0.0 event; from it onward, strict SemVer with deprecation fully resumes ([ADR004](./adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)).
- **Total cutoff of v4.x support** — no backport, not even for the concurrency bugs already documented ([ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)). v4.x stays frozen on GitHub tags and on already-published NuGet packages (which cannot be deleted, only deprecated); the only path to any fix is migrating to v5 ([ADR004](./adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)).
- **Multi-targeting net8.0/net9.0/net10.0 is kept in v5** — reaffirmed; a market-reach decision, not a compatibility one ([ADR002](./adr/ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md)).
- **Exclusive `IAsyncDisposable`** — `IDisposable` is removed in v5.0.0 ([ADR005](./adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)).
- **Concurrency**: the core is rewritten with `System.Threading.Channels` as a single state machine, replacing the current 3 primitives ([ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)).
- **Public fluent API surface**: redesign of `IRingBuffer`/`IRingBufferScaleCapacity`/`IRingBufferBuild` collapsing into explicit, type-level modes (`FixedCapacity`/`ElasticCapacity`) — the one item in the whole analysis whose blocker was purely compatibility cost, now removed and **decided** ([ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)).
- **Out of scope for v5** (a deliberate cut based on evidence/demand, not compatibility — see [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)): the median-based autoscaling algorithm ([ADR003](./adr/ADR003V01-median-sample-autoscaling-algorithm.md)) and native observability (Phase 7, backlog).
- **Documentation** will be restructured to be clear, objective, and instructive, with elevated priority for the v4→v5 migration guide (Phase 6).

## How to read this plan

| Column | Meaning |
|---|---|
| Priority | P0 = blocks the v5 release / operational risk. P1 = relevant structural gap. P2 = quality improvement/trade-off to formalize. |
| Effort | S (hours) / M (1-3 days) / L (>3 days) |
| ADR | Registered design decision (if any) |

**Execution order is no longer "independent parallel phases"** — Phase 1 (contract tests) is a hard prerequisite of Phase 2 (rewrite). See the diagram in the executive summary.

---

## Phase 0 — Immediate housekeeping (P1, effort S, no dependencies)

Items that are valid independently of v5 and can go into a small PR today, without waiting for the rewrite.

| # | Item | Action | Where |
|---|---|---|---|
| 0.1 | `SECURITY.md` out of date | Update the supported-versions table to `4.x` (and anticipate `5.x` at the v5 release) | `SECURITY.md` |
| 0.2 | Dead dependency contradicts stated policy | Remove `Microsoft.AspNetCore.Http.Abstractions` from the 3 csproj `ItemGroup`s (zero usage confirmed via grep) | `src/RingBufferPlus/RingBufferPlus.csproj` |
| 0.3 | No dedicated CHANGELOG | Create `CHANGELOG.md` (Keep a Changelog format) with the history currently in the README's "What's new" section. It will gain the "Breaking changes v5.0.0" section in Phase 6 | `CHANGELOG.md`, `README.md` |

**Acceptance criterion:** no behavior change; pure housekeeping.

---

## Phase 1 — Behavioral contract tests (P0, effort M, hard prerequisite of Phase 2)

**Why this comes before the rewrite, not after:** rewriting the concurrency core without a safety net is exactly the risk that, in the original analysis, argued *against* rewriting now. These tests describe the system's **intended** behavior (what `AcquireAsync`, `SwitchToAsync`, `WarmupAsync`, and `Dispose`/`DisposeAsync` must guarantee), not the current implementation — they exist specifically to capture the 7 concurrency bugs identified in the original review as *contract failures*, not as "behavior to preserve". See [ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md), "Mandatory sequencing" section.

| # | Contract to test | Motivated by (original bug) |
|---|---|---|
| 1.1 | Concurrent warmup (N simultaneous `AcquireAsync` calls on a cold buffer) produces exactly `Capacity` available items, never more, never less, with no spurious exception | Duplicate warmup race in `Startup()` |
| 1.2 | Cancelling the token during a scale operation (up or down) leaves the synchronization mechanism in a consistent state — no unhandled exception escapes the background task, and a subsequent scale operation works normally | `SemaphoreFullException` in `finally` |
| 1.3 | `_autoscaleRunning` (or equivalent state) never allows two conflicting scale operations in flight simultaneously, under any combination of concurrent `AcquireAsync`/`SwitchToAsync`/sample-based autoscale | Check-then-act outside lock |
| 1.4 | Dispose (sync or async) is idempotent, non-reentrant, and safe to call during an in-progress scale operation — no unhandled `ObjectDisposedException` escapes background tasks | `Dispose()` reentrancy via `Register` |
| 1.5 | Every internal queue/resource (equivalent to `_blockScale`) is released on cleanup — resource-leak test (handle/finalizer count, or direct state check if the new design exposes it) | `_blockScale` never disposed |
| 1.6 | `WarmupRingBufferAsync` (or its new-signature equivalent): the passed token is actually honored, and a missing buffer throws the documented exception | Ignored `token` / `ThrowIfNull` that never fires |
| 1.7 | `AcquireTimeout`, `AcquireDelayAttempts`, `LockWhenScaling` (true/false), and `SwitchToAsync` for all 3 targets (`Min/Max/InitCapacity`) keep the documented semantics in the new design | Today's shallow coverage (~169 lines) |

**Acceptance criterion:** a complete test suite, runnable against the current v4 code (most will fail — this is expected and documents the bugs), ready to serve as the objective acceptance criterion for Phase 2.

---

## Phase 2 — Rewrite the core and the public surface for v5 (P0, effort L, decision in [ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md), [ADR005](./adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md), and [ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md))

All design decisions needed for this phase are already closed (ADR001, ADR005, and ADR007 approved) — no item below depends on further maintainer decisions.

| # | Item | Action | Where |
|---|---|---|---|
| 2.1 | Replace the 3 primitives (semaphore + lock + BlockingCollection) | Redesign `RingBufferManager<T>` as a single state machine consuming a `System.Threading.Channels.Channel<T>` — a single loop owning `_currentCapacity`/scale state, with no explicit lock beyond channel ordering | `src/RingBufferPlus/Core/RingBufferManager.cs` (full rewrite) |
| 2.2 | Exclusive async-first disposal | Remove `IDisposable`; `IAsyncDisposable` as the sole contract on `IRingBufferService<T>` and `RingBufferValue<T>`; `DisposeAsync` written from scratch (without inheriting the `Register(() => Dispose())` pattern) | `RingBufferManager.cs`, `RingBufferValue.cs`, `Commands/IRingBufferService.cs` |
| 2.3 | Fix the `WarmupRingBufferAsync` signature | Actually pass the received `token` into `WarmupAsync`; fix the "buffer not found" check so it actually throws; remove the duplicated call | `src/RingBufferPlus/HostingExtensions.cs:56-73` |
| 2.4 | Update samples to `await using` | The 5 projects under `samples/*` use synchronous `using` today — migrate to the new async contract | `samples/*` |
| 2.5 | Validate against Phase 1 | Run the Phase 1 contract test suite against the new `RingBufferManager` — merge criterion is 100% green | — |
| 2.6 | Redesign the builder surface | Implement the design decided in [ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md): `.FixedCapacity(n)` / `.ElasticCapacity(min, max)` as distinct methods replacing `Capacity()`+`ScaleTimer()`; `AutoScaleAcquireFault(...)` returning a type without the manual `SwitchToAsync` equivalent; `LockWhenScaling` only exposed from `.ElasticCapacity(...)` | `Commands/*.cs`, `Core/RingBufferBuilder.cs` |
| 2.7 | Update samples/tests for the new builder | Every builder call site in `samples/*` and in the test suite needs to reflect the redesigned surface | `samples/*`, `src/RingBufferPlus.Tests/*` |

**Acceptance criterion:** all Phase 1 tests pass against the new code; none of the old concurrency primitives remain in the final design; the public surface no longer has any of the 4 silent interactions listed in [ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md).

---

## Phase 3 — CI/CD and test matrix (P0/P1, effort M, decision in [ADR002](./adr/ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md))

| # | Item | Action | Where |
|---|---|---|---|
| 3.1 | Tests only run on net10.0 | Change `<TargetFramework>net10.0</TargetFramework>` to `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>` (plural) — applies to the Phase 1 contract suite too | `src/RingBufferPlus.Tests/RingBufferPlus.Tests.csproj` |
| 3.2 | `dotnet test` needs to cover all 3 TFMs in CI | Confirm whether `dotnet test` runs all TFMs of the test project by default; if not, add a matrix `strategy` to the workflow or 3 explicit steps | `.github/workflows/build.yml` |
| 3.3 | Confirm whether the rewrite removes `#if NET9_0_OR_GREATER` | During Phase 2, check whether the new Channel-based design still needs the conditional `System.Threading.Lock` — document the outcome in the revision note of [ADR002](./adr/ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md#revision-note--v5-mandate-adr006) | `RingBufferManager.cs` |
| 3.4 | CI only validates ubuntu-latest | Add a matrix `os: [ubuntu-latest, windows-latest]` | `.github/workflows/build.yml` |
| 3.5 | No coverage report | Add `dotnet test --collect:"XPlat Code Coverage"` + artifact upload | `.github/workflows/build.yml` |

**Acceptance criterion:** a green pipeline running the full suite on all 3 TFMs, with no reduction in the library's compatibility scope.

---

## Phase 4 — Testable autoscale + benchmark (P1/P2, effort M, decision reaffirmed in [ADR003](./adr/ADR003V01-median-sample-autoscaling-algorithm.md))

Out of scope for the v5 rewrite by explicit decision — the median algorithm **does not change**, it only gains testability and measurement.

| # | Item | Action |
|---|---|---|
| 4.1 | Median calculation mixed with an async loop | Extract the median calculation + threshold decision into an isolated, testable pure/static method (independent of the Phase 2 rewrite, but natural to do in the same PR since the file is already being touched) |
| 4.2 | No unit tests for the statistical calculation | Tests with fixed arrays: even/odd sample counts, all equal, extreme values |
| 4.3 | No performance/reaction benchmark | Create a `RingBufferPlus.Benchmarks` project (BenchmarkDotNet) measuring reaction time to synthetic load changes, `AcquireAsync` throughput, scale up/down cost |

**Acceptance criterion:** documented benchmark results — the first quantitative evidence for the "resource-conscious usage" value proposition, and an objective input for deciding *in the future* (not now) whether a strategy pattern is worth adopting.

---

## Phase 5 — Release governance: v5 as a single reset + strict SemVer afterward (P0/P1, decision in [ADR004](./adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md))

| # | Item | Action | Where | When |
|---|---|---|---|---|
| 5.1 | Publish with no quality gate | `publish.yml` now depends on `build.yml` succeeding on the same commit (via `workflow_run` or `needs`) | `.github/workflows/publish.yml` | **Immediate** — applies already to the v5.0.0 release, not part of the "reset" |
| 5.2 | Tag with no format validation | Validate the regex `^v[0-9]+\.[0-9]+\.[0-9]+$` before `dotnet pack` | `.github/workflows/publish.yml` | **Immediate** |
| 5.3 | v5.0.0 release with no deprecation bridge | No `[Obsolete]` action is needed for the changes already authorized in [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — the release itself *is* the break | — | v5.0.0 only |
| 5.4 | Breaking-change communication | `CHANGELOG.md` with a dedicated "Breaking changes v5.0.0" section published together with the release (not after) | `CHANGELOG.md` | Together with 6.4 |
| 5.5 | Post-v5 deprecation policy | Document in `CONTRIBUTING.md`: from v5.0.0 onward, no public symbol is removed without `[Obsolete("migration message")]` for at least one release cycle | `CONTRIBUTING.md` | **From v5.1** onward |
| 5.6 | **Total cutoff of v4.x support** (decision confirmed by the maintainer: no backport) | Update `SECURITY.md`: only the latest major (v5.x onward) receives vulnerability fixes — v4.x and earlier marked unsupported, including the concurrency bugs already documented in [ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) | `SECURITY.md` | **At the v5.0.0 release** |
| 5.7 | Signal the cutoff on NuGet | The already-published v4.x packages **cannot be deleted** (NuGet only allows unlisting/deprecating) — evaluate marking the v4.x versions as "deprecated" on NuGet, pointing to v5.x, so whoever installs them isn't migrating blind | NuGet.org (package dashboard) | **At the v5.0.0 release** |

**Acceptance criterion:** a `v*` tag pointing at a commit with failing tests cannot publish to NuGet (test on a fork/dry-run); v5.0.0 is published with the CHANGELOG and migration guide already available at release time; `SECURITY.md` explicitly states v4.x no longer receives fixes.

---

## Phase 6 — Documentation restructuring (P0 for the migration guide, P1 for the rest; decision in [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md))

Current problem: `README.md` mixes pitch, changelog, tutorial, and reference in the same file (>350 lines); API documentation is generated without a consolidated architecture view; and now, with v5 being a sweeping breaking change, **the absence of a migration guide stopped being a quality gap and became a release blocker** — without it, no consumer can migrate.

| # | Item | Action | Where | Priority |
|---|---|---|---|---|
| 6.1 | **v4→v5 migration guide** | Dedicated document covering the 3 breaking-change fronts: exclusive `IAsyncDisposable` ([ADR005](./adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)), the redesigned builder surface ([ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)), and the fixed `WarmupRingBufferAsync` — a before/after example for each, and a migration checklist for each of the 6 usage scenarios currently documented in the README | `doc/guides/migration/v4-to-v5.md` | **P0 — blocks the release** |
| 6.2 | Overloaded README | Reduce `README.md` to: pitch (1 paragraph), installation, **one** minimal example ("Quickstart") already in `await using`, and links to the guides below | `README.md` → `doc/guides/*.md` | P1 |
| 6.3 | No single mental model before the API reference | Create `doc/guides/concepts.md`: lifecycle (build → warmup → acquire → scale up/down → `DisposeAsync`), the role of `Capacity`/`MinCapacity`/`MaxCapacity`, a (mermaid) diagram of the state flow in the new Channel-based design | `doc/guides/concepts.md` | P1 |
| 6.4 | Usage guides with no fixed structure | Every scenario guide follows the same template: **When to use → Minimal example (v5, async-first) → What happens internally → Trade-offs/limitations → Common errors** | `doc/guides/usage-*.md` | P1 |
| 6.5 | No architecture view for contributors | Create `doc/architecture/overview.md`: components (`Builder` → Channel-based `Manager` → `Service`), and a direct link to the ADRs in `doc/adr` as the source of truth | `doc/architecture/overview.md` | P1 |
| 6.6 | No thread-safety/DI guidance | Document usage as a singleton via `AddRingBuffer`, and "when NOT to use" scenarios | `doc/guides/concepts.md` | P2 |
| 6.7 | Link-rot risk | Add a markdown link check to CI (`markdown-link-check` or `lychee`) | `.github/workflows/build.yml` | P2 |

**Acceptance criterion:** (a) the v4→v5 migration guide covers 100% of the API changes listed in [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md); (b) a new contributor understands the mental model in `concepts.md` without reading code; (c) any design decision is traceable to its corresponding ADR.

---

## Phase 7 — Observability (P2, backlog, out of scope for v5 by explicit decision)

Not prioritized in this round. Registered as a candidate for a new ADR once there is real user demand (today there is only manual `ILogger`/`HeartBeat`). See the scope cut in [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md).

---

## Executive summary (execution order)

```
Phase 0 (housekeeping) ── parallel, can start before everything else

Phase 1 (contract tests) ──► Phase 2 (Channel-based + async-first rewrite)
        │                              │
        └── hard prerequisite ─────────┘
                                        │
                                        ▼
                              Phase 3 (CI covering net8/9/10)
                                        │
                                        ▼
                    ┌───────────────────┼───────────────────┐
                    ▼                   ▼                   ▼
         Phase 4 (testable       Phase 5 (v5 release:  Phase 6 (docs +
         autoscale+benchmark,    publish gate +        migration guide,
         out of v5 scope)        CHANGELOG)             P0 blocker)
                    │                   │                   │
                    └───────────────────┴───────────────────┘
                                        │
                                        ▼
                              v5.0.0 Release

Phase 7 (observability) ── backlog, not prioritized
```

**Change from the earlier version of this plan:** Phase 1 and Phase 3 (old numbering) swapped roles — concurrency tests now come **before** the code change, not after, because the code change stopped being "patch the 7 bugs" and became "complete rewrite" ([ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)). The migration guide (Phase 6.1) moved from "backlog" to "release blocker".

## Related ADRs

| ADR | Title | Status |
|---|---|---|
| [ADR001](./adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) | Concurrency model — Channel-based rewrite | **Accepted** (2026-08-11) |
| [ADR002](./adr/ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md) | Multi-targeting and test matrix | **Accepted** (2026-08-11) |
| [ADR003](./adr/ADR003V01-median-sample-autoscaling-algorithm.md) | Median-sample autoscaling | **Accepted** (2026-08-11) |
| [ADR004](./adr/ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) | Versioning — two-stage regime + total v4.x cutoff | **Accepted** (2026-08-11) |
| [ADR005](./adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) | Disposal — exclusive async-first | **Accepted** (2026-08-11) |
| [ADR006](./adr/ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) | **v5 mandate — no commitment to the current version** | **Accepted** (2026-08-11) |
| [ADR007](./adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md) | Redesign of the fluent API surface — explicit, type-level modes | **Accepted** (2026-08-11) |

All 7 ADRs have been formally approved (`adrplus approve`) as of 2026-08-11 — no design decision pending. Phase 2 can start with no approval blocker. Index automatically kept up to date at [`doc/adr/indexadrs.md`](./adr/indexadrs.md) by the `AdrIndexer` plugin.

### ADR necessity review (per maintainer request)

No ADR was removed. Each of the 7 governs a distinct, independent decision axis — removing any one of them would either lose a real decision or force it to be rebuilt as a loose paragraph elsewhere:

| ADR | Unique decision axis |
|---|---|
| ADR001 | Internal concurrency mechanism |
| ADR002 | Which runtimes to support (market reach) |
| ADR003 | Whether the scaling algorithm changes (evidence) |
| ADR004 | Version-stability policy over time |
| ADR005 | Disposal contract (sync vs. async) |
| ADR006 | Strategic mandate — what it authorizes and what it cuts from scope |
| ADR007 | Shape of the builder's public surface |

The one real overlap was ADR006 *restating* the content of ADRs 001/002/004/005 instead of just linking to them — fixed in this revision (ADR006's list is now one line + link per item, not a duplicated summary).
