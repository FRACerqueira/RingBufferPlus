# Usage: fixed capacity

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

You know the exact pool size you want and it never needs to change at runtime — a fixed number of pre-built, interchangeable instances of `T`, no autoscaling, no manual switching.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);

await using (var buffer = await rb.AcquireAsync(cancellation))
{
    if (buffer.Successful)
    {
        Console.WriteLine($"value: {buffer.Current}, elapsed: {buffer.ElapsedTime}");
    }
    else
    {
        // AcquireTimeout elapsed with no item available — handle the failure path
    }
}

await rb.DisposeAsync();
```

`FixedCapacity(n)` returns an `IRingBufferFixedBuilder<T>` — a distinct type from the elastic path, so `MonitorTuning`/`LockWhenScaling`/`SwitchToAsync` are not offered as options: they would be meaningless for a fixed pool, which has no elastic range to tune, pin, or switch within. `MinCapacity`/`MaxCapacity` are not offered as separate *options* either, but that's a different reason: there's only one capacity value to configure here, not that they're meaningless — both still exist on the built service (equal to `Capacity`), and the floor guard still reads and acts on them (see below).

## What happens internally

`BuildWarmupAsync` builds `n` items via `Factory` (respecting the per-call `Factory` timeout) and blocks asynchronously until all `n` are in the pool. From then on, `Capacity`, `MinCapacity`, and `MaxCapacity` all report the same value, and `IsInitCapacity`/`IsMinCapacity`/`IsMaxCapacity` are always `true` simultaneously — there is no *elastic* scale engine activity (no backlog-reactive signal, no Monitor, no `SwitchToAsync`) beyond the optional heartbeat/logger background tasks. The floor guard is the one exception: it still applies here too, and can produce real `scale.*` telemetry and the "below minimum capacity" log (see [the observability guide](usage-observability.md)) if a failed heartbeat- or `Invalidate()`-triggered replacement ever drops `CurrentCapacity` below this value.

## Trade-offs / limitations

- No elasticity: sustained demand above `n` concurrent acquisitions always waits for `AcquireTimeout` and fails, regardless of load — see [Concepts: Capacity, MinCapacity, MaxCapacity](concepts.md#capacity-mincapacity-maxcapacity).
- If your workload's demand actually varies, prefer [elastic manual scale](usage-elastic-manual-scale.md) or [elastic autoscale](usage-elastic-autoscale.md) instead of over-provisioning a fixed pool for the worst case.

## Common errors

- Calling `Build(cancellation)` instead of `BuildWarmupAsync` and then immediately calling `AcquireAsync` before the pool has been filled — the first acquires will wait for on-demand construction instead of finding a warm pool. Prefer `BuildWarmupAsync` unless you have a specific reason to defer warmup (see [ADR005](../adr/ADR005V01-async-disposal-strategy-and-graceful-shutdown.md)).
- Forgetting `Factory` — `Build`/`BuildWarmupAsync` throw `InvalidOperationException` if no factory was configured.
