<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Multi-targeting policy for net8 net9 net10 and test matrix|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Multi-targeting policy for net8/net9/net10 and test matrix

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision explicitly confirmed on 2026-08-11: keep multi-targeting.

Technical Story: Architecture review found that `src/RingBufferPlus/RingBufferPlus.csproj` multi-targets `net8.0;net9.0;net10.0`, but `src/RingBufferPlus.Tests/RingBufferPlus.Tests.csproj` is pinned to `<TargetFramework>net10.0</TargetFramework>` (singular) — `dotnet test` never builds or runs against net8.0/net9.0, even though the package is published for all three TFMs.

## Context and Problem Statement

Production code has TFM-conditional behavior (e.g. `#if NET9_0_OR_GREATER` in `RingBufferManager.cs`, which swaps `System.Threading.Lock` for `object` as the lock primitive on .NET 8). Without tests running on all three TFMs, that net8.0 compilation branch is never exercised by CI, even though it is exactly where concurrency bugs (see [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)) would most easily slip through undetected. Reducing multi-targeting would trivially close this coverage gap, but the maintainer ruled that out: the reach of consumers on net8.0 (LTS) and net9.0 already published on NuGet must be preserved.

## Decision Drivers

* Consumer reach already published on NuGet (net8.0 LTS is still widely used in production).
* TFM-conditional behavior in the concurrency core needs to be validated, not just compiled.
* Multiplying CI maintenance cost per TFM is acceptable against the risk of silent per-TFM regression.
* Explicit maintainer preference for keeping broad compatibility over reducing scope.

## Considered Options

* Reduce the library's multi-targeting to the newest supported TFM only (net10.0) and align tests to that.
* Keep multi-targeting net8.0/net9.0/net10.0 on the library and change `RingBufferPlus.Tests.csproj` to `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>` (plural), running the full suite on all three TFMs in CI.
* Keep multi-targeting on the library with a single net10.0 test project, accepting the coverage gap as a known, documented risk.

## Decision Outcome

Chosen option: "Keep multi-targeting net8.0/net9.0/net10.0 on the library and change tests to the same matrix", because it is the only option that preserves the already-published consumer reach (explicit maintainer decision) while still closing the coverage gap that triggered the review. Resulting action: change `RingBufferPlus.Tests.csproj` to a plural `TargetFrameworks` with the three TFMs, and adjust `.github/workflows/build.yml` so `dotnet test` runs (by default, or via an explicit `--framework <tfm>`, to be validated empirically) the suite against all three targets, failing the build on any TFM regression.

### Positive Consequences

* No reduction in the package's market reach.
* The `#if NET9_0_OR_GREATER` branch (and any other current or future TFM-conditional code) becomes exercised by CI on all three TFMs.
* Increases confidence to ship the concurrency fixes (see [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)) without silent regressions on net8.0/net9.0.

### Negative Consequences

* CI run time increases (build + test 3x instead of 1x per push/PR).
* Any real BCL behavior divergence between TFMs will require conditional (`#if`) tests on the test side too, increasing suite complexity.

## Pros and Cons of the Options

### Reduce to net10.0 only

* Good, because it drastically simplifies CI and tests.
* Bad, because it breaks compatibility for consumers already on net8.0/net9.0 — explicitly rejected by the maintainer.

### Keep multi-targeting and expand tests to the same matrix (chosen)

* Good, because it closes the coverage gap without reducing reach.
* Good, because it validates TFM-conditional code that today is only compiled, never executed in CI.
* Bad, because it increases CI time and the maintenance surface of the suite.

### Keep the coverage gap as an accepted risk

* Good, because it is the lowest-effort option in the short term.
* Bad, because it leaves exactly the riskiest code (concurrency, see [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)) without a safety net on 2 of the 3 published TFMs — rejected.

## Revision note — v5 mandate ([ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md))

With the mandate for a complete product overhaul for v5.0.0 (sweeping breaking changes authorized), this decision was re-examined and **reaffirmed without change**: net8.0/net9.0/net10.0 remain supported in v5. The `#if NET9_0_OR_GREATER` currently present in `RingBufferManager.cs` (swapping `System.Threading.Lock` for `object` as the lock primitive) would, in theory, motivate dropping net8.0 if the Channel-based rewrite ([ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)) made that `#if` unnecessary and depended on other net9+-only APIs. That possibility was evaluated, and the maintainer explicitly chose to keep all three TFMs regardless of the rewrite's technical outcome — a market-reach decision, not a technical-feasibility one.

**Outcome (Phase 2, 2026-08-11):** the Channel-based rewrite ([ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)) removed the `#if NET9_0_OR_GREATER` / `System.Threading.Lock` conditional entirely — the new single-consumer engine design needs no lock primitive at all, on any TFM. This confirms, rather than changes, the decision above: net8.0/net9.0/net10.0 all build and pass the full test suite identically, so keeping all three TFMs remains a pure market-reach choice with no remaining technical asymmetry between them.

## Links

* Related: [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — the most critical TFM-conditional code today is precisely the lock primitive in `RingBufferManager.cs`; the Channel-based rewrite must keep net8.0 compatibility regardless of whether it eliminates that `#if`.
* Related: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — mandate under which this decision was re-examined and reaffirmed.
