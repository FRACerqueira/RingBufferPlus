# Usage: locking SwitchToAsync while scaling

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

By default, `SwitchToAsync` returns as soon as the scale operation it triggered is *scheduled*, not once it has finished — the caller doesn't wait for the buffer to actually reach the new capacity. If your scenario needs to know whether the target capacity was actually, fully reached before proceeding, use `LockWhenScaling()`.

**`AcquireAsync` is unaffected either way** — items already in the pool are always immediately acquirable, whether or not a scale operation is in flight and regardless of this setting. `LockWhenScaling` changes nothing about acquisition; it only changes what `SwitchToAsync`'s caller waits for.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .ElasticCapacity(3, 9, 6)
    .LockWhenScaling()
    .BuildWarmupAsync(cancellation);

// with LockWhenScaling(): returns only after the scale-up finishes, one way or the other -
// true if it fully reached MaxCapacity, false if it only partially completed before its own
// timeout (whatever capacity was actually gained is kept either way, not undone)
var reached = await rb.SwitchToAsync(ScaleSwitch.MaxCapacity, TimeSpan.FromMinutes(5));
```

`LockWhenScaling(bool value = true)` is not part of `IRingBufferFixedBuilder<T>` at all — a fixed-capacity buffer never scales, so there's nothing for the setting to affect. It is part of `IRingBufferElasticBuilder<T>`, and since v6.0.0 ([ADR001V03](../adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md)/[ADR007V03](../adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md)) that is the only elastic builder — every elastic pool has `SwitchToAsync` available, so this setting has an observable effect on every elastic pool, not a specific "manual switch" mode. In no case does it affect `AcquireAsync`.

## What happens internally

`SwitchToAsync` always posts a command to the engine loop and waits for that command to be *accepted* (i.e., scheduled). Acceptance is immediate only when the engine is idle — if another scale operation (of any kind - manual, floor-guard, backlog-reactive, or Monitor-driven) is already in flight, this call is **rejected outright, not queued** (ADR001V03: at most one such batch runs at a time), so it returns `false` right away without scheduling anything. Otherwise, with `LockWhenScaling()` disabled (the default), it returns `true` immediately after acceptance, without waiting for the move itself to finish (a scale-up's own deadline is `quantity × FactoryTimeout`; a scale-down has no deadline for finding items to remove, it only takes whatever is already idle - though disposing each removed item is still separately bounded by `PulseHeartBeat`, see [the heartbeat guide](usage-heartbeat.md)). With `LockWhenScaling()` enabled, it additionally awaits the scale operation's own completion signal, so the returned `bool` reflects whether the buffer *fully* reached the target capacity (`true`) or only partially completed before its own deadline (`false`) — a partial result is never undone, whatever capacity was actually gained or removed is kept (see [ADR010](../adr/ADR010V01-allow-partial-capacity-gains-when-a-scale-up-cannot-fully-complete.md)). `AcquireAsync` never consults this flag — see [ADR001V03](../adr/ADR001V03-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md).

## Trade-offs / limitations

- Without `LockWhenScaling`, a `true` result from `SwitchToAsync` only means "scheduled," not "reached" — if your code assumes `IsMaxCapacity` is `true` immediately after `await SwitchToAsync(ScaleSwitch.MaxCapacity)` returns, enable `LockWhenScaling` or poll `IsMaxCapacity` separately.
- With `LockWhenScaling`, `SwitchToAsync` takes as long as the scale operation itself (bounded by its own timeout) — don't enable it on a hot path that can't afford to wait out a scale-up.

## Common errors

- Assuming `LockWhenScaling` throttles or serializes `AcquireAsync` calls during a scale — it does not affect acquisition at all, only the completion semantics of `SwitchToAsync`.
- Treating a `false` result from `SwitchToAsync` as always meaning "rejected" — with `LockWhenScaling` enabled, `false` can also mean "accepted, but the move itself only partially completed before its own deadline" (the partial progress is kept, not undone).
