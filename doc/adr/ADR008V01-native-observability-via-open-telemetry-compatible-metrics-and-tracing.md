<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Native observability via OpenTelemetry-compatible metrics and tracing|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Native observability via OpenTelemetry-compatible metrics and tracing

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision confirmed on 2026-08-11: option "Native BCL-only metrics/tracing (`System.Diagnostics.Metrics` + `ActivitySource`), no OpenTelemetry package dependency".

Technical Story: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) explicitly excluded native observability from v5 scope, citing "lack of demonstrated user demand" — not cost or compatibility — and registered it as Action Plan Phase 7, "a candidate for a new ADR once there is real user demand." This ADR is that candidate. The signal that triggered it is not an external consumer request; it is the maintainer's own cost/benefit read, made explicitly during this conversation, once the concrete implementation cost was scoped out: additive, no new dependency, no breaking change. Recording this honestly matters, because it is a different kind of trigger than what ADR006 anticipated — see the revision note added to ADR006 alongside this ADR.

## Context and Problem Statement

Today, the only way to see what a `RingBufferManager<T>` is doing internally — current capacity, acquire faults, scale-up/scale-down events, acquire latency — is the `Logger`/`OnError`/`HeartBeat` callbacks a caller wires up themselves, which produce free-text log lines. There is no structured, exportable signal: no out-of-the-box metric a Grafana/Prometheus or Application Insights dashboard can plot, no span that correlates a slow request trace with "the ring buffer was mid-scale at that moment." Anyone who wants that today has to hand-roll a log scraper.

## Decision Drivers

* No new runtime dependency is required: `System.Diagnostics.Metrics.Meter` and `System.Diagnostics.ActivitySource` have shipped as part of the .NET shared framework since .NET 5 — referencing them from a `net8.0`/`net9.0`/`net10.0` class library needs no additional `PackageReference`. Verified empirically against `net8.0` (the oldest target): a probe referencing both types built successfully with no new package appearing in `dotnet list package --include-transitive`, confirming they resolve from `Microsoft.NETCore.App`, not a transitive dependency.
* Both APIs are "pay for play" by design: with no `MeterListener`/`ActivityListener` attached (the common case for a consumer who doesn't care about observability), the instrumentation calls are near-zero-cost. This is the same mechanism ASP.NET Core, `HttpClient`, and `System.Net.Sockets` already use for their own built-in instrumentation — it is a proven, idiomatic .NET pattern, not a novel design.
* Purely additive: no existing public member changes shape or behavior. This does not need to wait for a future major or carry a deprecation cycle under [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — it can ship as soon as it's ready, including bundled into the still-unreleased v5.0.0.
* `RingBufferManager<T>` already carries every event this would expose (`LogMessage`/`LogWarning`/`LogError` call sites, `MoveToCapacityAsync`, `AcquireAsync`) — instrumentation is a matter of adding emission at existing call sites, not discovering new integration points.

## Considered Options

* **Keep status quo** — `Logger`/`OnError`/`HeartBeat` only, no structured telemetry.
* **Native, BCL-only instrumentation** — `System.Diagnostics.Metrics.Meter` for metrics and `System.Diagnostics.ActivitySource` for tracing, both already in the shared framework; RingBufferPlus takes no dependency on any OpenTelemetry package itself. A consumer who wants export adds whatever OpenTelemetry SDK/exporter (or any other `System.Diagnostics.DiagnosticSource`-compatible collector) they choose, on their own side.
* **Direct dependency on the `OpenTelemetry` NuGet package** — call its API directly from `RingBufferManager<T>`.
* **Vendor-specific integration** — e.g. a direct Application Insights SDK dependency.

## Decision Outcome

**Decided on 2026-08-11 by the maintainer:** "Native, BCL-only instrumentation" (option 2), because it delivers the same consumer-visible outcome (metrics and traces an OpenTelemetry Collector can ingest) as option 3, without locking every consumer's exporter choice — or the SDK major version they're pinned to — to whatever the `OpenTelemetry` package's own release cadence happens to be, and without the vendor lock-in of option 4. It also happens to add no new `PackageReference` at all (see the verified driver above), which is a nice property but not the deciding one on its own — the library already carries `Microsoft.Extensions.Logging.Abstractions`/`Microsoft.Extensions.Hosting.Abstractions`, so "zero dependencies" was never the literal bar. "OpenTelemetry-compatible" in this ADR's title means exactly this: emits what the OpenTelemetry .NET SDK (or anything else built on `System.Diagnostics.DiagnosticSource`) already knows how to bridge — not that RingBufferPlus depends on OpenTelemetry.

**Concrete design to implement in Action Plan Phase 7:**
* One `Meter` and one `ActivitySource` **per `RingBufferManager<T>` instance** — both fields, both constructed with the same constant `Name`, `"RingBufferPlus"`. Multiple `Meter`/`ActivitySource` instances sharing one `Name` is legal and normal in `System.Diagnostics`; a listener subscribing to that name sees instruments/activities from every live buffer without needing to know how many exist. This is a deliberate departure from "one static source for the whole assembly" (unlike `System.Net.Http`'s single shared `Meter`): a per-instance `Meter` is disposed cleanly from that instance's own `DisposeAsync()`, with no shared-lifetime hazard (disposing one buffer must never silence another) and no need for a live-instance registry to back the observable gauge below — its callback closes over `this` directly.
* Metrics, all tagged with `buffer.name` (redundant with instance ownership, but needed so a listener/exporter watching the shared `"RingBufferPlus"` name can still tell buffers apart):
  * `ringbufferplus.acquire.duration` (`Histogram<double>`) — the same elapsed time already computed for `RingBufferValue<T>.ElapsedTime`, additionally tagged `acquire.success` (bool).
  * `ringbufferplus.acquire.faults` (`Counter<long>`) — no `_total` suffix: most Prometheus-family exporters append that themselves, and a source name ending in `_total_total` is a known rough edge to avoid up front.
  * `ringbufferplus.capacity.current` (`ObservableGauge<int>`) — callback reads `CurrentCapacity` on the owning instance.
  * `ringbufferplus.scale.operations` (`Counter<long>`) — tagged `direction` (up/down) and `trigger` (manual/auto); same no-`_total` reasoning as above.
  * `ringbufferplus.scale.duration` (`Histogram<double>`) — tagged `direction`.
* Tracing: one `Activity` per `AcquireAsync` call (tagged `buffer.name`, `success`, `timed_out`) and one per scale operation (tagged `buffer.name`, `direction`, `trigger`), both started/stopped around the existing call sites in `RingBufferManager<T>`.
* The instance's `Meter` and `ActivitySource` are disposed from that same instance's `DisposeAsync()`, alongside its other owned resources (channels, `CancellationTokenSource`) — ordinary per-instance cleanup, not a shared-lifetime concern.
* No public API changes: nothing about this is opt-in/opt-out from the builder surface — instrumentation always emits; whether it costs anything or goes anywhere depends entirely on whether the consuming process has a listener attached, which is not RingBufferPlus's concern.
* Verification: extend `RingBufferContractTests.cs` (or a new `RingBufferObservabilityTests.cs`) with `MeterListener`/`ActivityListener`-based assertions that the expected instruments/activities fire with the expected tags. Extend the existing `benchmarks/RingBufferPlus.Benchmarks` project with a throughput comparison (listener attached vs. none) to substantiate the "near-zero-cost when unused" claim with real numbers rather than assumption.
* Documentation: a new `doc/guides/usage-observability.md` following the same template as the other Phase 6 usage guides, plus a mention in `doc/architecture/overview.md`.
* Whichever release actually ships this needs an "Added" entry in `CHANGELOG.md` — not written yet, since this ADR only decides the design, it doesn't implement it (see Action Plan Phase 7).

### Positive Consequences

* Closes a real, previously-identified gap (ADR006's own problem statement) without taking on a dependency, a breaking change, or a deprecation cycle.
* Reuses infrastructure this project already built for a different purpose (Phase 4's benchmark harness, Phase 1/2's contract-test patterns) rather than inventing new verification machinery.
* Because it's additive and non-breaking, it is not gated behind a future major — it can ship in v5.0.0 itself if the timeline allows, rather than waiting for v5.1.0.

### Negative Consequences

* Adds new call sites inside `RingBufferManager<T>`'s hot paths (`AcquireAsync`, `MoveToCapacityAsync`) that must be kept genuinely near-zero-cost when unobserved — the benchmark comparison above is not optional polish, it is the evidence this ADR's own performance claim rests on.
* `"ringbufferplus.*"` is a project-specific naming choice, not an existing OpenTelemetry semantic convention (none exists yet for generic resource pools) — if OTel ever standardizes one, these names may need to be revisited under a future ADR.

## Pros and Cons of the Options

### Keep status quo (rejected)

* Good, because it is zero additional work.
* Bad, because it leaves the exact gap ADR006 already identified, unresolved, with no plan to resolve it beyond "wait for demand" — and the maintainer has now decided the cost of resolving it doesn't justify further waiting.

### Native, BCL-only instrumentation (chosen)

* Good, because it adds no new `PackageReference` at all — a strictly smaller footprint than the alternatives, even though the library already isn't literally dependency-free.
* Good, because it is additive/non-breaking, so it doesn't need to wait for a future major.
* Good, because it is exporter-agnostic — consumers pick whatever OpenTelemetry SDK version/exporter fits their stack.
* Bad, because RingBufferPlus owns the responsibility of keeping the instrumentation itself cheap when unobserved — mitigated by the benchmark evidence required above.

### Direct `OpenTelemetry` package dependency (rejected)

* Good, because it's a well-known, widely-adopted API surface.
* Bad, because it forces every consumer onto the `OpenTelemetry` package's own major/release cadence, even those who never enable observability — a dependency for a feature that costs nothing when unused, per option 2.
* Bad, because it ties RingBufferPlus's supported versions to that package's own release/major cadence, instead of staying exporter-agnostic.

### Vendor-specific integration (rejected)

* Bad, because it locks every consumer into one specific APM vendor regardless of what they actually use.
* Bad, because it is the least reusable of all options — the exact opposite of "OpenTelemetry-compatible."

## Links

* Refined by: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — this ADR is the "candidate ADR once there is real user demand" that ADR006's scope cut anticipated; see ADR006's revision note.
* Related: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — this change is additive and does not trigger the deprecation-cycle policy.
* Related: `CONTRIBUTING.md` — the "no dependencies beyond the BCL" rule this design deliberately preserves.
