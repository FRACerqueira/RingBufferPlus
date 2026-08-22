<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Concurrency model for RingBufferManager scale up and down|
|Version|03|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-22)|
|Changed|Accepted (2026-08-22)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Concurrency model for RingBufferManager scale up/down: single owner, bounded executors

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — decision on 2026-08-22, under the [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate for v6.0.0.

Technical Story: [ADR001V02](./ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) established the principle this version generalizes rather than reopens: a single owner mutates all capacity state, satellites only send commands and report completion. v5.0.0's shipped engine already runs 4 cooperating tasks (`_engineTask`, `_heartbeatTask`, `_sampleTickTask`, `_loggerTask`) over 3 channels under that single-owner principle — it was never literally "one thread". v6.0.0 needs two more roles (a bounded item-creation executor and a dedicated disposal executor) plus a floor-safety rule and a backlog-driven reactive signal, and the design analysis needed to establish precisely how those additions preserve, rather than violate, the single-owner principle.

## Context and Problem Statement

A review of this v6.0.0 proposal (conducted before any implementation) flagged that a naive reading of "more roles" (Orquestrador, Fábrica, Monitor, plus a new Remoção role for disposal) as "three-or-four independent peer actors" would reopen exactly the class of bug ADR001V02 was written to close: `CurrentCapacity` is a public property, and if Pool/Fábrica/Monitor/Remoção could each independently mutate capacity-related state, no single actor would hold the authoritative in-flight count, risking a scale-up request and a Monitor-driven scale-down decision landing concurrently and pushing capacity above `MaxCapacity` — the pool's central contract ("bounded"). Separately, the current reactive trigger (`AutoScaleAcquireFault`, fault-count based) was measured (via the real, shipped `AutoScaleReactionBenchmarks`, mean 77.46 ms) to be dominated by the configured `AcquireTimeout`, not by the actual cost of scaling — a slow, coarse signal by construction. How do these additions get built without forfeiting the single-owner guarantee ADR001V02 already earned through 8 rounds of hardening (121 findings)?

## Decision Drivers

* The single-owner principle from ADR001V02 is validated by real, expensive hardening work (8 audit rounds) — reopening it needs a specific, stated reason, not an incidental side effect of adding roles.
* `CurrentCapacity` and the capacity-state properties (`IsMinCapacity`/`IsMaxCapacity`/`IsInitCapacity`) are public contract — their truthfulness cannot depend on reconciling multiple independent writers.
* The current reactive signal (fault-count) reacts only after a caller has already waited out `AcquireTimeout` — a real, measured latency cost with no compensating benefit.
* Disposal (`Remoção`) can block on real I/O (a DB/AMQP connection closing), the same reasoning that already justified isolating instance creation (`Fábrica`) from the state-owning loop.
* A large backlog spike must not translate into an equally large burst of simultaneous factory calls against a downstream that may itself be degraded, not merely under-provisioned.

## Considered Options

* Keep the exact v5.0.0 4-task/3-channel shape; add the new floor-guard and backlog-reactive logic directly inside the existing engine task, without new named roles.
* Generalize to four named roles — **Orquestrador** (single owner), **Fábrica** (bounded creation executor), **Remoção** (disposal executor), **Monitor** (pure signal source) — each satellite only executing commands or reporting computed values, with the Orquestrador as the sole mutator of authoritative state.
* Treat Orquestrador, Fábrica, Remoção, and Monitor as symmetric peers, each capable of adjusting capacity directly when its own logic determines it should.

## Decision Outcome

Chosen option: "Generalize to four named roles, Orquestrador as sole owner" — because it names and documents the structural boundary that was previously implicit inside a single engine task, without weakening it: the reason to give Fábrica, Remoção, and Monitor their own names is that they now have distinct, independently-reasoned-about responsibilities (bounded concurrent creation, isolated disposal, and predictive computation, respectively — see [ADR003V03](./ADR003V03-median-sample-autoscaling-algorithm.md) for the Monitor's algorithm), not that they become independent decision-makers over shared state. The third option (symmetric peers) is explicitly rejected: it is exactly the failure mode that would reopen ADR001V02's closed class of bug.

**Concrete design:**

* **Orquestrador** — owns `Channel<T>` exclusively; the only actor that ever increments or decrements the authoritative capacity counter, and only on a satellite's *confirmed completion*, never on the request that asked for it. Tracks available / in-flight-creating / in-flight-removing. Derives `CurrentCapacity` and the capacity-state properties from this single source.
* **Floor guard**: `available ≤ MinCapacity` triggers an immediate, undebounced replenishment request — highest priority of all signals, since it protects the pool's minimum contractual floor. The public "below minimum" property only becomes true if that replenishment fails to restore `MinCapacity` within one `FactoryTimeout` cycle (reusing the existing parameter as the grace window, adding no new one): a transient dip self-heals invisibly; a genuinely broken factory is reported truthfully, not instantly on the first touch of the floor.
* **Backlog-reactive signal**: replaces fault-count-based triggering. The Orquestrador tracks how many callers are currently waiting to acquire; when waiting exceeds available capacity beyond a small debounce, it requests exactly the net gap (`waiting − available − in-flight-creating`, always deducting what is already requested, so overlapping backlog waves never duplicate a request) from Fábrica — reacting before any `AcquireTimeout` elapses, proportional to the real gap, not a coarse tier jump.
* **Fábrica** — executes "create N" commands. Runs the user factory with its existing per-call `FactoryTimeout`, applies a simple growing backoff after consecutive failures (self-protection against hammering a broken factory, not a circuit-breaker state machine), and reports completed count plus last fault. **Concurrency is bounded by a new parameter, `MaxConcurrentFactoryCalls`** (small sane default) — this is the thundering-herd mitigation: a large backlog spike cannot flood a struggling-but-technically-accepting downstream with simultaneous connection attempts; if the downstream really is unhealthy, the throttled attempts themselves start failing and trip the existing backoff, closing the loop without new circuit-breaker machinery. Average/percentile lease duration is exposed as an observability metric (via the existing `Meter`/`ActivitySource`, [ADR008](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md)) so an operator can distinguish "pool undersized" from "downstream slow" — the library reports this truthfully, it does not infer or act on the distinction itself.
* **Remoção** — executes "dispose these N items" commands, where the Orquestrador has already dequeued the items from the channel before handing them off (Remoção never touches the channel directly). Isolated because `DisposeAsync` on a real connection can block on I/O — the same reasoning ADR001V02 already applied to isolating Fábrica.
* **Monitor** — purely computational; samples a signal, computes a target, reports it to the Orquestrador. Never mutates state directly. See [ADR003V03](./ADR003V03-median-sample-autoscaling-algorithm.md) for its algorithm.
* **Signal priority inside the Orquestrador**: floor guard > backlog-reactive > manual pin (while active, see [ADR007V03](./ADR007V03-redesign-of-the-public-fluent-api-surface.md)) > Monitor (predictive).

### Positive Consequences

* Preserves the single-owner guarantee that took 8 audit rounds to earn, while giving each new responsibility (bounded creation, isolated disposal, prediction) a clear name and boundary.
* Reactive latency no longer includes waiting out `AcquireTimeout` — reacts on real backlog depth instead.
* Thundering-herd risk against a degraded (not under-provisioned) downstream is bounded by construction (`MaxConcurrentFactoryCalls`), reusing the existing factory-backoff mechanism instead of new circuit-breaker state.
* The floor guard's grace window reuses `FactoryTimeout` — no new configuration surface for a transient-vs-genuine distinction.

### Negative Consequences

* `MaxConcurrentFactoryCalls` is a genuinely new configuration parameter — accepted because it is a standard, well-understood concept in every real connection-pool design (bounded concurrent connection creation), unlike the tri-state fault classification + circuit-breaker considered and rejected earlier in this analysis for scaling library complexity with how badly-written a consumer's factory is.
* The floor guard's grace window means a genuinely broken factory is reported one `FactoryTimeout` cycle later than an instantaneous flag would — an explicit, bounded trade-off in favor of not treating every transient blip as a full outage.
* Four named roles are more surface to reason about than one engine task, even though only one of them (Orquestrador) mutates shared state — onboarding and testing need to make the asymmetry (owner vs. executors) explicit, not just implicit in code comments.

## Pros and Cons of the Options

### Keep the v5.0.0 shape, add logic inline (rejected)

* Good, because it changes the least code.
* Bad, because it does not give the new responsibilities (bounded creation, isolated disposal) a testable boundary of their own — they would stay implicit inside an already-large engine task.

### Four named roles, Orquestrador as sole owner (chosen)

* Good, because it names the boundary explicitly without changing who is allowed to mutate state.
* Good, because bounded creation and isolated disposal each become independently reasoned-about and testable.
* Bad, because it is more surface than a single engine task, and the owner/executor asymmetry must be actively maintained, not assumed.

### Symmetric peer actors (rejected)

* Good, because it maximizes each role's local autonomy.
* Bad, because it reopens the exact class of bug ADR001V02 was written to close — no single source of truth for capacity, real risk of exceeding `MaxCapacity` under concurrent reactive and predictive requests.

## Links

* Refines: [ADR001V02](./ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) — generalizes the single-owner principle to four named roles; does not reopen it.
* Authorized by: [ADR006V02](./ADR006V02-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — v6.0.0 breaking-change mandate.
* Related: [ADR003V03](./ADR003V03-median-sample-autoscaling-algorithm.md) — the Monitor role's algorithm.
* Related: [ADR007V03](./ADR007V03-redesign-of-the-public-fluent-api-surface.md) — the manual pin that participates in signal priority here.
* Related: [ADR008V01](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md) — carries the lease-duration diagnostic metric.
