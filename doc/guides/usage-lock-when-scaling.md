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
    .ElasticCapacity(6, 3, 9)
    .LockWhenScaling()
    .BuildWarmupAsync(cancellation);

// with LockWhenScaling(): returns only after the scale-up finishes, one way or the other -
// true if it fully reached MaxCapacity, false if it only partially completed before its own
// timeout (whatever capacity was actually gained is kept either way, not undone)
var reached = await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
```

`LockWhenScaling(bool value = true)` is not part of `IRingBufferFixedBuilder<T>` at all — a fixed-capacity buffer never scales, so there's nothing for the setting to affect. It has an observable effect only on `IRingBufferElasticBuilder<T>` (manual switch, no `AutoScaleAcquireFault`), where `SwitchToAsync` actually exists. As of v5.1.0, it is also removed entirely from `IRingBufferAutoScaleBuilder<T>` — it used to be retained there as a documented no-op purely so the fluent chain still compiled after `AutoScaleAcquireFault(...)`, but that builder's service never had a `SwitchToAsync` to wait on, so the setting could never do anything there; chaining `.LockWhenScaling()` after `.AutoScaleAcquireFault()` is now a compile error instead of a silent no-op (see [ADR007](../adr/ADR007V02-redesign-of-the-public-fluent-api-surface.md)). In no case does it affect `AcquireAsync`.

## What happens internally

`SwitchToAsync` always posts a command to the engine loop and waits for that command to be *accepted* (i.e., scheduled). Acceptance is immediate only when the engine is idle — if another scale operation is already in flight, this command queues behind it on the single-consumer engine, so acceptance can take as long as that operation's own deadline (a scale-up's deadline is `quantity × FactoryTimeout`, not `baseTimer`/`numberSamples` — those only configure the scale-down sampling cadence; a scale-down has no deadline for finding items to remove, it only takes whatever is already idle - though disposing each removed item is still separately bounded by `PulseHeartBeat`, see [the heartbeat guide](usage-heartbeat.md)) before this call even gets scheduled. With `LockWhenScaling()` disabled (the default), it then returns `true` immediately after acceptance, without waiting for the move itself to finish. With it enabled, it additionally awaits the scale operation's own completion signal, so the returned `bool` reflects whether the buffer *fully* reached the target capacity (`true`) or only partially completed before its own deadline (`false`) — a partial result is never undone, whatever capacity was actually gained or removed is kept (see [ADR010](../adr/ADR010V01-allow-partial-capacity-gains-when-a-scale-up-cannot-fully-complete.md)). `AcquireAsync` never consults this flag — see [ADR001](../adr/ADR001V02-concurrency-model-for-ring-buffer-manager-scale-up-and-down.md) and the CHANGELOG's "Breaking changes v5.0.0" entry for the full v4→v5 behavior change on this point.

## Trade-offs / limitations

- Without `LockWhenScaling`, a `true` result from `SwitchToAsync` only means "scheduled," not "reached" — if your code assumes `IsMaxCapacity` is `true` immediately after `await SwitchToAsync(ScaleSwitch.MaxCapacity)` returns, enable `LockWhenScaling` or poll `IsMaxCapacity` separately.
- With `LockWhenScaling`, `SwitchToAsync` takes as long as the scale operation itself (bounded by its own timeout) — don't enable it on a hot path that can't afford to wait out a scale-up.

## Common errors

- Assuming `LockWhenScaling` throttles or serializes `AcquireAsync` calls during a scale — it does not affect acquisition at all, only the completion semantics of `SwitchToAsync`.
- Treating a `false` result from `SwitchToAsync` as always meaning "rejected" — with `LockWhenScaling` enabled, `false` can also mean "accepted, but the move itself only partially completed before its own deadline" (the partial progress is kept, not undone).
