# Usage: pinning capacity manually

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md), [Elastic autoscale](usage-elastic-autoscale.md)

## When to use

Since v6.0.0 ([ADR001V03](../adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)/[ADR007V03](../adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md)), every elastic pool always has the floor guard, backlog-reactive signal, and Monitor active — there is no separate "manual-only" mode to opt into instead. `SwitchToAsync` is a **temporary pin**: use it when you know something the Monitor's own sliding-window/trend model does not yet — an upcoming scheduled event, an external signal you already watch — and want to hold a specific capacity for a bounded window instead of waiting for the Monitor to catch up on its own.

## Minimal example

```csharp
var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .ElasticCapacity(minCapacity: 3, maxCapacity: 9, target: 6)
    .BuildWarmupAsync(cancellation);

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(10)))
{
    // scale was not scheduled — see "Common errors" below
}

// ... later, once the pin's own duration has done its job ...
await rb.SwitchToAsync(ScaleSwitch.InitCapacity, TimeSpan.FromMinutes(1));

await rb.DisposeAsync();
```

`ElasticCapacity(...)` returns an `IRingBufferElasticBuilder<T>`, which is where `MinCapacity`/`MaxCapacity` become meaningful and both `SwitchToAsync` (via the built `IRingBufferManualScaleService<T>`) and `MonitorTuning(...)` are always available together — there is no mutually exclusive alternative builder to choose instead.

## What happens internally

`SwitchToAsync` posts a command to the engine's single-consumer loop and requires an explicit `pinDuration`: for that long, it substitutes for the Monitor's own predictive output, so the Monitor will not move capacity away from wherever this call (or the floor guard/backlog-reactive signal acting during the pin) leaves it. There is no default duration — a caller must decide one explicitly every time (see [ADR007V03](../adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md) for why). By default `SwitchToAsync` returns `true` as soon as the move is *scheduled*, without waiting for it to finish — but "scheduled" itself is only immediate when the engine is idle; a call arriving while another scale operation (of any kind - manual, floor-guard, backlog-reactive, or Monitor-driven) is already in flight is rejected outright, not queued (a scale-up's own deadline is `quantity × FactoryTimeout`, a scale-down never waits at all; see the [lock guide](usage-lock-when-scaling.md) for the full detail). Enable `LockWhenScaling()` if you need the returned `bool` to reflect whether the target capacity was actually, fully reached. Either way, asking to switch to the capacity the buffer is already at returns `false` immediately, with no pin set and nothing scheduled — and `AcquireAsync` is never blocked by an in-progress scale, regardless of `LockWhenScaling`.

## Trade-offs / limitations

- The floor guard and backlog-reactive signal are never suppressed by an active pin — a real waiting caller or a floor breach is still addressed immediately regardless of what capacity you pinned. Only the Monitor's own predictive scaling is held off for the pin's duration.
- **A scale-**down** pin that can't fully complete right away (not enough idle items - some are still checked out) is not retried while the pin is active.** Nothing besides the Monitor would finish the reduction as retained items become idle again, and the Monitor is exactly what a pin suppresses - so `CurrentCapacity` can stay above the value you pinned for the whole pin duration, even though the invariant it never violates (`[MinCapacity, MaxCapacity]`, always truthful) still holds. This is a `LogWarning` (`"...only partially completed... while a manual pin is active..."`), not silent, and self-corrects once the pin expires and ordinary Monitor ticks resume - but a caller on the default (non-`LockWhenScaling`) path has no way to observe it directly other than that log. Known, accepted behavior, not planned to change without a further decision.
- `pinDuration` must be greater than `TimeSpan.Zero`, or `SwitchToAsync` throws `ArgumentOutOfRangeException` before scheduling anything.
- `SwitchToAsync` on a plain `IRingBufferService<T>` reference (e.g. after an upcast/downcast around the type system) throws `InvalidOperationException` if the buffer has a fixed capacity — the compile-time exclusivity between `FixedCapacity` and manual switching is enforced at runtime too for anyone who works around the static type.

## Common errors

- Calling `SwitchToAsync(ScaleSwitch.MaxCapacity, duration)` when already at `MaxCapacity` and treating the `false` result as a failure — it isn't; it means there was nothing to do (and no pin was set).
- Passing a `target` outside `[minCapacity, maxCapacity]` to `ElasticCapacity` — this throws `InvalidOperationException` at `Build`/`BuildWarmupAsync` time, not at the `ElasticCapacity` call itself.
- Passing a zero or negative `pinDuration` — throws `ArgumentOutOfRangeException` immediately, before anything is scheduled.
