# Usage: elastic capacity with autoscale on acquire fault

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md) · [ADR003](../adr/ADR003V01-median-sample-autoscaling-algorithm.md)

## When to use

Demand is not predictable enough to schedule manual switches — you want the buffer to grow on its own when callers are timing out trying to acquire, and shrink back on its own once usage no longer justifies the extra capacity.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .AcquireTimeout(TimeSpan.FromMilliseconds(500))
    .ElasticCapacity(initialCapacity: 3, minCapacity: 2, maxCapacity: 4, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(5))
    .AutoScaleAcquireFault(numberOfFaults: 2)
    .BuildWarmupAsync(cancellation);

// no SwitchToAsync here — rb is a plain IRingBufferService<int>, not IRingBufferManualScaleService<int>
```

`AutoScaleAcquireFault(numberOfFaults)` returns an `IRingBufferAutoScaleBuilder<T>`. `Build`/`BuildWarmupAsync` from that type return a plain `IRingBufferService<T>` — `SwitchToAsync` is not part of its surface, by design ([ADR007](../adr/ADR007V01-redesign-of-the-public-fluent-api-surface.md)).

## What happens internally

- **Scale up:** every timed-out `AcquireAsync` increments a fault counter in the engine loop. Once the counter exceeds `numberOfFaults`, the buffer moves to `Capacity` if it is currently below `Capacity` (a partial recovery step - this also covers a fresh buffer where `initialCapacity == minCapacity`), or straight to `MaxCapacity` otherwise, and the counter resets. Because faults are counted per timed-out *acquire call*, not per internal retry as in v4, reaction time is roughly `AcquireTimeout × numberOfFaults` — lower `AcquireTimeout` if you need faster reaction (see the `CHANGELOG.md` "Breaking changes v5.0.0" entry on this behavior change).
- **Scale down:** on every sampling tick (`baseTimer` / `numberSamples` — e.g. above, one sample roughly every 100ms, evaluated as a median every 5 seconds), the engine computes the median of collected samples via `AutoScaleDecision.Median` and decides whether to step back down toward `MinCapacity`/`Capacity` via `AutoScaleDecision.EvaluateScaleDown` — see [ADR003](../adr/ADR003V01-median-sample-autoscaling-algorithm.md) for the exact thresholds and why the algorithm is a pure, independently-tested function.
- Scale-up has a deadline of `quantity × FactoryTimeout` (the factory's own per-item timeout, set via `Factory(value, timeout)`); scale-down never waits at all - it only takes whatever is already idle right now. Neither direction "undoes" a partial result: a scale-up that only creates some of the requested items keeps them, and a scale-down that only finds some items idle removes just those - see [ADR001](../adr/ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md).

## Trade-offs / limitations

- `SwitchToAsync` is unreachable from the returned type; escaping the type system (casting to `IRingBufferManualScaleService<T>`) and calling it anyway throws `InvalidOperationException` rather than silently doing nothing.
- Very aggressive `numberSamples`/`baseTimer` combinations increase background sampling overhead for marginal gains in reaction precision — see the `AutoScaleReactionBenchmarks` project under `benchmarks/RingBufferPlus.Benchmarks/` for measured numbers before tuning these away from the defaults.
- Reaction time is bounded below by `AcquireTimeout × numberOfFaults` for scale-up to be *triggered*, and by `quantity × FactoryTimeout` for the scale-up itself to *complete* — this is not a sub-millisecond autoscaler. That same `FactoryTimeout` also bounds how long the engine can be busy creating items before it's free to react to anything else (faults, another scale request) - its 15-second default is inherited from a previous release and not calibrated against any particular factory; set it deliberately based on your own factory's real latency.

## Common errors

- Setting `AcquireTimeout` very high while expecting fast scale-up — the two are coupled; see "What happens internally" above.
- Passing `numberSamples < 1` or a `baseTimer`/`numberSamples` combination implying less than 100ms per sample — both throw `InvalidOperationException` at `Build`/`BuildWarmupAsync` time.
