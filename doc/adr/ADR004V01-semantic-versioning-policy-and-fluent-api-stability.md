<!-- Do not remove this comment, lines and table (1-12) -->
|Adr-Plus Fields|Values Migrated |
|--|--|
|File title md|Semantic versioning policy and fluent API stability|
|Version|01|
|Revision||
|Scope||
|Domain||
|Created|Proposed (2026-08-11)|
|Changed|Accepted (2026-08-11)|
|Superseded||
<!-- Do not remove this comment, lines and table (1-12) -->
---
# Semantic versioning policy and fluent API stability

## Deciders

* Deciders: Fernando Cerqueira (maintainer)

Technical Story: The README history (`README.md`, "What's new" section) shows command renames on every major: v3.2.0 renamed `MasterScale`→`ScaleUnit`; v4.0.0 removed `Master/Slave`, `ReportScale`, `BufferHealth`, `ScaleWhen...`, `RollbackWhen...`, `TriggerByAccqWhen...`, and renamed `ScaleUnit`→`ScaleTimer`, all without a prior `[Obsolete]` period. In addition, `.github/workflows/publish.yml` publishes any `v*` tag without depending on `.github/workflows/build.yml` succeeding on the same commit, and without validating the tag's semver format.

**Revised on 2026-08-11 under the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate:** the maintainer authorized v5.0.0 as a complete product overhaul, with sweeping breaking changes. This makes this ADR's original wording ("strict SemVer + `[Obsolete]` before removing, always") self-contradictory for v5 itself — one cannot require a prior deprecation cycle for a rewrite that, by mandate, is itself the breaking event. The policy needs to be split into two distinct regimes (see revised Decision Outcome).

**Revised again on 2026-08-11 — support policy for previous versions:** the "no commitment to the current version" mandate ([ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)) implies a question the earlier wording of this ADR did not answer: what happens to v4.x (and earlier majors) *after* v5.0.0 ships? The natural answer, consistent with the mandate, is: **total freedom only exists because there is no obligation to maintain compatibility or continued support for previous versions**. v4.x stops receiving any update from the v5.0.0 release onward — including the concurrency bugs already identified and documented in [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) (e.g. the `SemaphoreFullException` that kills autoscale, the duplicate warmup race). The maintainer explicitly confirmed: **total cutoff, no backport** — no fix will reach v4.x; the only path to any fix is migrating to v5.

**Revised again on 2026-08-11 — migration communication scope:** the maintainer decided against producing a dedicated `doc/guides/migration/v4-to-v5.md` document (Action Plan, Phase 6, item 6.1 as originally scoped). This is **not** derived from the [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate — "no commitment to the current version" answers whether v5.0.0 may break compatibility, not whether existing consumers are told how to move past the break; ADR006 itself lists "existing consumers still need clear notice and a migration path" as a decision driver and "requires a robust v4→v5 migration guide" as a named negative consequence. This is a separate, deliberate scope/effort decision: `CHANGELOG.md`'s `[Unreleased]`/"Breaking changes v5.0.0" section is the **sole** migration reference — no separate guide, no per-scenario checklist, no before/after walkthroughs beyond what the CHANGELOG entries themselves state. The "Mandatory communication" item below and [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)'s negative consequences are updated accordingly.

## Context and Problem Statement

The package is already published on NuGet with real consumers (download badge in the README). Every unannounced breaking change on a major has real migration cost for those consumers — and today nothing in the publish pipeline stops a `v*` tag from publishing a commit with broken tests. How do we formalize an API stability policy and a publish gate that (a) accommodates the single, deliberate reset of v5.0.0 authorized by [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md), and (b) does not become a permanent blank check for breaking changes on every future major?

## Decision Drivers

* Real external consumers already depend on the public fluent API (`IRingBuffer<T>`, `IRingBufferScaleCapacity<T>`, etc.).
* The [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) mandate has already authorized sweeping breaking changes for v5.0.0 — this ADR cannot contradict it, only define what comes after.
* The migration cost of unannounced breaking changes is high and damages trust in the package — but only *after* the single reset.
* `publish.yml` today has no quality gate before publishing to NuGet — an operational risk independent of the breaking-change policy, applying to v5 and to every version after it.
* Single maintainer (bus factor, already flagged in the original analysis) — maintaining two branches in parallel (a patched v4.x plus active v5.x development) has a real attention cost that "total freedom" is meant to avoid, not just in the API but in the support process too.
* v5.0.0's freedom is only coherent if paired with no obligation of continued support for previous versions — otherwise "no commitment to the current version" on the API side, combined with an indefinite obligation to fix bugs in v4.x, would be a self-contradictory half-freedom.

## Considered Options

* Keep breaking changes free on every major, with no prior `[Obsolete]`, forever (status quo extended indefinitely).
* Two-stage regime: (a) v5.0.0 is the single, deliberate reset authorized by [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — no `[Obsolete]` bridge, no parallel V2 namespace; (b) from v5.0.0 onward (including v5.1, v6, etc.), resume strict SemVer with a mandatory deprecation cycle before any public removal.
* Freeze the current (pre-v5) public fluent API and move all design evolution to a parallel namespace/major (e.g. `RingBufferPlus.V2`), never breaking the published package.

## Decision Outcome

Chosen option: "Two-stage regime", because it is the only option compatible with the mandate already given in [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) while still preserving predictable stability for the post-v5 future. Detail:

**Stage (a) — v5.0.0, single reset:**
* No `[Obsolete]` bridge is required for the changes already authorized in [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) (Channel-based concurrency, exclusive `IAsyncDisposable`, the `WarmupRingBufferAsync` signature fix, the surface redesign from [ADR007](./ADR007V01-redesign-of-the-public-fluent-api-surface.md)).
* No parallel (`V2`) namespace — v5.0.0 replaces the current API directly.
* Mandatory communication: `CHANGELOG.md` with a dedicated "Breaking changes v5.0.0" section, published *together with* the release, not after. Per the revision note above, this CHANGELOG section is the sole migration reference — no separate migration guide document.
* **Support policy for previous versions (total cutoff, decision confirmed by the maintainer):** v4.x (and earlier majors) receive no fix after the v5.0.0 release — not even the concurrency bugs already documented in [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md). There is no "one last courtesy" v4.0.2. Already-published releases remain available: (i) as GitHub tags/branches, as history; (ii) as already-published NuGet packages — which **cannot be deleted** (NuGet only allows unlisting/deprecating, never removal), so they remain installable even without receiving updates. `SECURITY.md` and `CONTRIBUTING.md` must explicitly state that only the latest major (v5.x onward) receives vulnerability/bug fixes (see Action Plan, Phase 5).

**Stage (b) — from v5.0.0 onward, strict SemVer resumed (follow-up action, see Action Plan, Phase 5):**
1. Document the policy in `CONTRIBUTING.md`: no public symbol is removed without `[Obsolete("migration message")]` for at least one release cycle.
2. Change `publish.yml` to only run after `build.yml` has succeeded on the same commit (via `workflow_run` or by consolidating the workflows with `needs`) — **this item applies immediately, including to the v5.0.0 release itself**, it is not part of the "reset".
3. Validate that the tag follows `vMAJOR.MINOR.PATCH` before `dotnet pack` — **also applies immediately**.

### Positive Consequences

* v5.0.0 can fix the structural debt at its root without the self-contradiction of requiring deprecation of itself.
* From v5.0.0 onward, consumers regain advance notice and a migration path before any removal — the instability is a single window, not a pattern.
* Eliminates the risk of publishing a package with a broken build/tests, regardless of the breaking-change regime.

### Negative Consequences

* v4.x consumers absorb a concentrated, real migration cost at v5.0.0 — partially mitigated by a detailed `CHANGELOG.md` breaking-changes section (no separate migration guide, per the revision note above), so the cost is higher than the original plan anticipated, by deliberate choice.
* More process discipline required from the single maintainer from v5.0.0 onward (see the bus-factor trade-off already flagged in the original analysis).
* Risk of the "single reset" becoming an informal precedent for future majors if it is not reinforced as exceptional — mitigated by this ADR explicitly stating that regime (a) does not repeat.
* Consumers unable to migrate immediately to v5.0.0 remain permanently exposed to the concurrency bugs already documented in [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — a conscious maintainer decision (total cutoff, no backport), not an oversight.

## Pros and Cons of the Options

### Extended status quo (breaking changes always free)

* Good, because it is the lowest-effort process.
* Bad, because it destroys the trust any SemVer policy tries to build, and has already caused documented friction (renames on every major) — rejected.

### Two-stage regime: v5 as a single reset + strict SemVer after, with total cutoff of v4.x support (chosen)

* Good, because it is consistent with the mandate already authorized ([ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md)) without self-contradiction.
* Good, because it confines instability to a single, explicit event, not a permanent pattern.
* Good, because the publish gate (item 2/3 of stage b) closes a real operational risk, independent of the versioning regime.
* Good, because the total cutoff of v4.x support avoids the half-freedom of having a fully new API while still maintaining two parallel code lines — consistent with the single-maintainer bus factor.
* Bad, because it requires discipline to keep the "single reset" from becoming a recurring excuse in future majors.
* Bad, because whoever does not migrate gets no fix at all on v4.x — mitigated only by clear CHANGELOG/release-notes communication, not by a transition patch.

### Freeze the pre-v5 API and create a parallel V2

* Good, because it would give maximum stability to v4.x consumers.
* Bad, because it directly contradicts the mandate already given in [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) (which authorizes direct replacement, not two APIs coexisting) — discarded.

## Links

* Refined by: [ADR006](./ADR006V01-mandate-for-a-complete-product-overhaul-in-v5-with-authorized-breaking-changes.md) — mandate that authorizes stage (a) of this ADR.
* Related: [ADR001](./ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) — the Channel-based rewrite is one of the changes covered by stage (a).
* Related: [ADR005](./ADR005V01-async-disposal-strategy-and-graceful-shutdown.md) — exclusive `IAsyncDisposable` is another change covered by stage (a).
* Related: [ADR003](./ADR003V01-median-sample-autoscaling-algorithm.md) — observable behavior changes to scaling also follow stage (b) from v5.0.0 onward.
