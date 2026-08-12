<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Mandate for a complete product overhaul in v5 with authorized breaking changes|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Mandate for a complete product overhaul in v5.0.0 with authorized breaking changes

## Deciders

* Deciders: Fernando Cerqueira (maintainer) — explicit decision on 2026-08-11, reaffirmed and strengthened on 2026-08-11: v5.0.0 has **no commitment whatsoever** to the current version — the goal is to improve the product, even if that means total incompatibility with v4.x. This is not merely "breaking changes allowed where necessary"; it is the removal of compatibility as a decision criterion.

Technical Story: ADRs 001, 003, 004, 005 and 007 were initially decided (or identified as a gap, in the case of 007) under the constraint "do not break the public API" (breaking-change cost treated as prohibitive). The maintainer removed that constraint entirely for the next major: **v5.0.0 is a complete product overhaul**, not an incremental, additive accumulation of patches, and not a commitment to preserve anything from v4.x that is not justified on its own technical merit. This ADR is the umbrella mandate that re-anchors the earlier decisions and the new one.

## Context and Problem Statement

Real concurrency bugs (see [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md)), public API surface defects (`WarmupRingBufferAsync` with an ignored `token` and an `ArgumentNullException` that never fires — `HostingExtensions.cs:56-73`), and a hybrid disposal model (blocking synchronous `IDisposable`, see [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)) could today only be fixed *within* the existing public signatures, producing additive fixes that preserve design debt. With breaking changes authorized, these items can be fixed at the root (signature redesign), not just in internal behavior. The question is: should this become a permanent precedent of "any major can break anything", or a single, coordinated window?

## Decision Drivers

* Accumulated technical debt from additive retrofits has a higher long-term cost than a coordinated rewrite.
* A window for sweeping breaking changes is rare and expensive to reopen — worth consolidating multiple design fixes at once.
* Existing consumers still need clear notice and a migration path — "authorized" does not mean "at no managed cost".
* Without an explicit scope cut, the temptation to "take the opportunity to change more things" could inflate v5 indefinitely.

## Considered Options

* Continue with incremental additive patches, never breaking the public API (the earlier regime, see ADRs 001/004/005 in their original version).
* Authorize v5.0.0 as a coordinated, deliberate rewrite — a single design "reset" — resuming strict SemVer with deprecation from v5.0.0 onward.
* Treat every future major as an opportunity for free breaking changes, never resuming deprecation discipline afterward.

## Decision Outcome

Chosen option: "v5.0.0 as a single, coordinated, deliberate rewrite, resuming strict SemVer from it onward", because it consolidates the structural fix into a single migration cycle for consumers (less fatigue than breaking changes spread across several majors) without opening a precedent of permanent instability — "no commitment to the current version" describes v5.0.0 itself, not a permanent state of the project. This ADR authorizes, for v5.0.0, each item below (details and trade-offs live in each item's own ADR — this list does not repeat them, only references them):

* **Concurrency** — Channel-based rewrite, see [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md).
* **Disposal** — exclusive `IAsyncDisposable`, removal of `IDisposable`, see [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md).
* **Public fluent API surface** (builder) — redesign of `IRingBuffer`/`IRingBufferScaleCapacity`/`IRingBufferBuild` collapsing into explicit, type-level modes (`FixedCapacity`/`ElasticCapacity`), decision closed, see [ADR007](./ADR007V01-redesign-of-the-public-fluent-api-surface.md).
* **`HostingExtensions.WarmupRingBufferAsync`** — fix the signature/contract at its root (`token` currently ignored, a null check that never throws), covered as part of the surface redesign in [ADR007](./ADR007V01-redesign-of-the-public-fluent-api-surface.md).
* **Multi-targeting** — reaffirmed, net8.0/net9.0/net10.0 kept, see [ADR002](./ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md).

Explicitly **out of scope** for this breaking-change window (a deliberate scope cut, to avoid inflating v5). Note that under "no commitment to the current version", compatibility is **no longer a valid reason** to exclude anything — so each exclusion below is justified by a different criterion, independent of compatibility:

* **The median-based autoscaling algorithm** — excluded for **lack of evidence**, not for compatibility cost: there is no benchmark demonstrating that swapping the algorithm would improve the product (see [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md)). If a benchmark later shows a need, the exclusion is revisited — with data, not with "since it can break anyway, might as well".
* **Native observability (OpenTelemetry)** — excluded for **lack of demonstrated user demand**, not for compatibility cost. It remains in the backlog, without its own ADR yet, until there is a real signal of need. **Revisited on 2026-08-11 — see [ADR008](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md):** the signal that ended up triggering the candidate ADR was not an external consumer request as this bullet anticipated — it was the maintainer's own cost/benefit assessment once the concrete implementation cost was scoped out (additive, no new dependency, no breaking change). Recorded here for traceability rather than silently treated as if "demonstrated user demand" had literally occurred.

### Positive Consequences

* Consolidates multiple structural fixes into a single migration cycle, avoiding distributed breaking-change fatigue.
* Fixes public API surface bugs at their root (signature), not just palliatively.
* Preserves net8/9/10 consumer reach (no runtime compatibility loss, only API).

**Revised on 2026-08-11 — migration communication scope:** the maintainer decided against a dedicated v4→v5 migration guide document — see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) for the detailed rationale. This does not follow from "no commitment to the current version" — that phrase authorizes breaking compatibility, it says nothing about whether the break is documented for existing consumers. `CHANGELOG.md`'s "Breaking changes v5.0.0" section is now the sole migration reference; the negative consequence below is updated to reflect that deliberately smaller scope, not removed.

### Negative Consequences

* Requires explicit communication of the breaking changes via `CHANGELOG.md`'s dedicated "Breaking changes v5.0.0" section (published with the release, not after) — no separate migration guide document is produced (see the revision note above); the breaking change is still broad and needs to be publicly justified, just through a narrower communication surface than originally planned.
* Rewriting the concurrency core without today's existing test safety net is a real risk — mitigated by requiring behavioral contract tests *before* the rewrite.
* Scope-creep risk — mitigated by the explicit "out of scope" list above; any additional item requires its own ADR, it does not get in by inertia.

## Pros and Cons of the Options

### Incremental additive patches (earlier regime)

* Good, because it never breaks existing consumers.
* Bad, because it perpetuates a hybrid design (3 concurrency primitives, duplicated disposal) — exactly what produced the bugs identified in the original review.

### v5.0.0 as a single coordinated rewrite (chosen)

* Good, because it fixes the structural debt at its root instead of retrofitting.
* Good, because it has an explicit scope cut and resumes SemVer discipline afterward — it is not a permanent blank check.
* Bad, because it concentrates migration risk into a single release, demanding robust communication and tests.

### Free breaking changes on every future major

* Good, because it gives maximum design flexibility at any time.
* Bad, because it destroys the trust the SemVer policy (see [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md)) tries to build — rejected.

## Links

* Refines: [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — Channel-based rewrite authorized for v5.
* Refines: [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md) — decision to keep the median reaffirmed; exclusion justified by lack of evidence, not compatibility.
* Refines: [ADR004](./ADR004V01-semantic-versioning-policy-and-fluent-api-stability.md) — defines the two regimes (v5 as a single reset; strict SemVer from it onward).
* Refines: [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) — exclusive `IAsyncDisposable` authorized for v5.
* Refines: [ADR007](./ADR007V01-redesign-of-the-public-fluent-api-surface.md) — fluent surface redesign authorized and decided (explicit, type-level modes).
* Related: [ADR002](./ADR002V01-multi-targeting-policy-for-net8-net9-net10-and-test-matrix.md) — net8/9/10 multi-targeting reaffirmed, unaffected by this mandate.
* Refined by: [ADR008](./ADR008V01-native-observability-via-open-telemetry-compatible-metrics-and-tracing.md) — the observability exclusion above is revisited; see this ADR's revision note.
