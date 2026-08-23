# Usage: elastic autoscale

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md) · [Pinning capacity manually](usage-elastic-manual-scale.md) · [ADR001V03](../adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) · [ADR003V03](../adr/ADR003V03-median-sample-autoscaling-algorithm.md)

## When to use

You want the buffer to react to demand on its own: grow immediately when callers are genuinely waiting, and grow or shrink over time as the Monitor's own sliding-window model tracks a trend. Since v6.0.0, this is not something you opt into — every elastic pool gets it automatically.

## Minimal example

```csharp
var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .AcquireTimeout(TimeSpan.FromMilliseconds(500))
    .ElasticCapacity(minCapacity: 2, maxCapacity: 4, target: 3, numberSamples: 50, baseTimer: TimeSpan.FromSeconds(5))
    .MonitorTuning(percentileP: 0.95, safetyBuffer: 0.10, horizon: 5, deadband: 3) // optional - these are the defaults
    .BuildWarmupAsync(cancellation);

// rb is an IRingBufferManualScaleService<int> - SwitchToAsync is also available (see the pin guide),
// on top of the automatic signals below, which are already running.
```

## What happens internally

Three signals are always active for an elastic pool, in this priority order ([ADR001V03](../adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)):

1. **Floor guard** — if `CurrentCapacity` ever drops below `MinCapacity` (only possible today via a failed heartbeat- or `Invalidate()`-triggered single-item replacement), the guard replenishes it immediately, retrying until it succeeds or logging an error once a full `FactoryTimeout` cycle has elapsed without recovering.
2. **Backlog-reactive** — a caller that cannot be served immediately reports itself as real-time demand the instant it starts waiting, well before `AcquireTimeout` would even elapse. The target is `min(CurrentCapacity + (waiting - idle), MaxCapacity)`: it reacts proportionally to the actual unmet demand, not a coarse jump to `MaxCapacity`.
3. **Monitor** (lowest priority) — on every sampling tick (`baseTimer` / `numberSamples`, e.g. above one sample roughly every 100ms over a 5-second window), a demand sample (in-use items + waiting callers) joins a sliding window. Once the window has at least 2 samples, a target is computed from a percentile of the window (`percentileP`, inflated by `safetyBuffer`) adjusted by a linear-regression trend projected `horizon` ticks ahead, clamped to `[MinCapacity, MaxCapacity]`. A move only dispatches once the difference from `CurrentCapacity` exceeds `deadband` — without it, the algorithm oscillates under flat-but-noisy demand. Unlike the other two signals, the Monitor can scale *down* as well as up, and it's the only one that can scale up before demand has actually exceeded capacity (a rising trend alone can trigger it). All four parameters are tunable via `MonitorTuning(...)`; see its own XML doc for the exact defaults and trade-offs.

At most one scale batch (of any kind — floor, backlog, Monitor, or a manual pin via `SwitchToAsync`) is ever in flight at once; a signal that fires while another batch is running is deferred, not dropped — it re-evaluates once that batch completes. Scale-up has a deadline of `quantity × FactoryTimeout` (the factory's own per-item timeout, set via `Factory(value, timeout)`); scale-down never waits for more items to become idle - it only takes whatever is already idle right now, disposing each one in the background bounded by `PulseHeartBeat` (configurable via `HeartBeat(callback, pulse: ...)`'s `pulse` parameter, defaulting to 10 seconds even if `HeartBeat` itself is never configured - see [the heartbeat guide](usage-heartbeat.md)) - a hung `Dispose()`/`DisposeAsync()` on a removed item is logged and left to finish in the background rather than blocking the scale-down forever. Neither direction "undoes" a partial result: a scale-up that only creates some of the requested items keeps them, and a scale-down that only finds some items idle removes just those.

A manual pin (`SwitchToAsync`, see [the pin guide](usage-elastic-manual-scale.md)) substitutes for the Monitor's own output for an explicit duration, but never suppresses the floor guard or backlog-reactive signal.

## Trade-offs / limitations

- Very aggressive `numberSamples`/`baseTimer` combinations increase background sampling overhead for marginal gains in precision. The window's real-time span is always exactly `baseTimer` regardless of `numberSamples` — see `ElasticCapacity`'s own XML doc for why lowering `numberSamples` alone does not shorten the Monitor's reaction horizon.
- The Monitor's own sliding window only clears when an actual scale operation fires (gated by `deadband`) - during a genuinely steady period, nothing clears it, so a demand drop can take up to `baseTimer` to be reflected once the window is full. The floor guard and backlog-reactive signal already cover the more urgent cases (a real waiting caller, a floor breach) far faster than this, regardless.
- `maxConsecutiveFactoryFailures` (set via `Factory(value, timeout, maxConsecutiveFactoryFailures)`) combines multiplicatively with `FactoryTimeout`'s own reaction-time trade-off when the tolerated failures are themselves timeouts, not fast exceptions: tolerating `N` consecutive timeouts before giving up on an item raises that item's own worst-case blocking time to roughly `(N + 1) × FactoryTimeout`, on top of `quantity × FactoryTimeout` for the whole batch.
- Honoring the `CancellationToken` your `Factory` delegate receives is your responsibility, not this library's: the token fires at the per-item `FactoryTimeout` deadline (and on shutdown), but a factory that ignores it and keeps working anyway just keeps running, orphaned, after the engine has already given up waiting on it. If it eventually succeeds, the instance it produces is discarded without being disposed — a leaked database connection, broker channel, or similar, that this library cannot detect or clean up on your behalf. Pass the token through to any awaited I/O inside your factory to avoid this.

## Common errors

- Passing `numberSamples < 1` or a `baseTimer`/`numberSamples` combination implying less than 100ms per sample — both throw `InvalidOperationException` at `Build`/`BuildWarmupAsync` time.
- Passing an out-of-range `MonitorTuning(...)` argument (`percentileP` outside `(0, 1]`, a negative `safetyBuffer`/`horizon`/`deadband`) — throws `InvalidOperationException` at `Build`/`BuildWarmupAsync` time, not at the `MonitorTuning` call itself.
