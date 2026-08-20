# Usage: elastic capacity with manual scale

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

Demand is predictable enough (or externally signaled — a schedule, a metric you already watch, an admin action) that *you* decide when to scale, rather than letting the buffer react to acquisition pressure on its own.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .ElasticCapacity(initialCapacity: 6, minCapacity: 3, maxCapacity: 9)
    .BuildWarmupAsync(cancellation);

if (!await rb.SwitchToAsync(ScaleSwitch.MaxCapacity))
{
    // scale was not scheduled — see "Common errors" below
}

// ... later, e.g. once load has passed ...
await rb.SwitchToAsync(ScaleSwitch.InitCapacity);

await rb.DisposeAsync();
```

`ElasticCapacity(...)` returns an `IRingBufferElasticBuilder<T>`, which is where `MinCapacity`/`MaxCapacity` become meaningful and `SwitchToAsync` becomes available on the built service — as long as you don't also call `AutoScaleAcquireFault` (that combination is covered by the [autoscale guide](usage-elastic-autoscale.md), and the two are mutually exclusive by design, see [ADR007](../adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)).

## What happens internally

`SwitchToAsync` posts a command to the engine's single-consumer loop. By default it returns `true` as soon as the move is *scheduled*, without waiting for it to finish — but "scheduled" itself is only immediate when the engine is idle; behind an already in-flight scale operation, this call queues and can take as long as that operation's own timeout (`baseTimer`, 30 seconds by default) before it returns at all (see the [lock guide](usage-lock-when-scaling.md) for the full detail). Enable `LockWhenScaling()` if you need the returned `bool` to reflect whether the target capacity was actually reached. Either way, asking to switch to the capacity the buffer is already at returns `false` immediately, with nothing scheduled — and `AcquireAsync` is never blocked by an in-progress scale, regardless of `LockWhenScaling`.

## Trade-offs / limitations

- Nothing scales unless you tell it to — there is no reaction to acquire faults or sampled usage in this mode. If you want that, see [elastic autoscale](usage-elastic-autoscale.md).
- `ElasticCapacity`'s `numberSamples`/`baseTimer` parameters are validated at `Build`/`BuildWarmupAsync` time (they still throw on an invalid combination) but have zero runtime effect here - the sampling pump they configure only starts when `AutoScaleAcquireFault` is also enabled. In manual-only mode they are pure dead weight; there is no reason to tune them.
- `SwitchToAsync` on a plain `IRingBufferService<T>` reference (e.g. after an upcast/downcast around the type system) throws `InvalidOperationException` if the builder that produced it went through `AutoScaleAcquireFault` — the compile-time exclusivity is enforced at runtime too for anyone who works around the static type.

## Common errors

- Calling `SwitchToAsync(ScaleSwitch.MaxCapacity)` when already at `MaxCapacity` and treating the `false` result as a failure — it isn't; it means there was nothing to do.
- Passing an `initialCapacity` outside `[minCapacity, maxCapacity]` to `ElasticCapacity` — this throws `InvalidOperationException` at `Build`/`BuildWarmupAsync` time, not at the `ElasticCapacity` call itself.
