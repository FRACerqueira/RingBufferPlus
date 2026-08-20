# Usage: locking SwitchToAsync while scaling

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

By default, `SwitchToAsync` returns as soon as the scale operation it triggered is *scheduled*, not once it has finished — the caller doesn't wait for the buffer to actually reach the new capacity. If your scenario needs to know the target capacity was actually reached (or the operation was undone on timeout) before proceeding, use `LockWhenScaling()`.

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

// with LockWhenScaling(): returns only after the buffer has actually reached MaxCapacity
// (or the scale-up was undone on timeout, reflected in the false result)
var reached = await rb.SwitchToAsync(ScaleSwitch.MaxCapacity);
```

`LockWhenScaling(bool value = true)` is not part of `IRingBufferFixedBuilder<T>` at all — a fixed-capacity buffer never scales, so there's nothing for the setting to affect. It has an observable effect only on `IRingBufferElasticBuilder<T>` (manual switch, no `AutoScaleAcquireFault`), where `SwitchToAsync` actually exists. `IRingBufferAutoScaleBuilder<T>` also exposes the method — purely so the fluent chain still compiles after `AutoScaleAcquireFault(...)` — but it is a documented no-op there: that builder's service has no `SwitchToAsync` to wait on. In no case does it affect `AcquireAsync`.

## What happens internally

`SwitchToAsync` always posts a command to the engine loop and waits for that command to be *accepted* (i.e., scheduled). Acceptance is immediate only when the engine is idle — if another scale operation is already in flight, this command queues behind it on the single-consumer engine, so acceptance can take as long as that operation's own timeout (`baseTimer`, 30 seconds by default) before this call even gets scheduled. With `LockWhenScaling()` disabled (the default), it then returns `true` immediately after acceptance, without waiting for the move itself to finish. With it enabled, it additionally awaits the scale operation's own completion signal, so the returned `bool` reflects whether the buffer actually reached the target capacity (`true`) or the move was undone on timeout (`false`). `AcquireAsync` never consults this flag — see [ADR001](../adr/ADR001V01-concurrency-model-for-ringbuffermanager-scale-up-and-down.md) and the CHANGELOG's "Breaking changes v5.0.0" entry for the full v4→v5 behavior change on this point.

## Trade-offs / limitations

- Without `LockWhenScaling`, a `true` result from `SwitchToAsync` only means "scheduled," not "reached" — if your code assumes `IsMaxCapacity` is `true` immediately after `await SwitchToAsync(ScaleSwitch.MaxCapacity)` returns, enable `LockWhenScaling` or poll `IsMaxCapacity` separately.
- With `LockWhenScaling`, `SwitchToAsync` takes as long as the scale operation itself (bounded by its own timeout) — don't enable it on a hot path that can't afford to wait out a scale-up.

## Common errors

- Assuming `LockWhenScaling` throttles or serializes `AcquireAsync` calls during a scale — it does not affect acquisition at all, only the completion semantics of `SwitchToAsync`.
- Treating a `false` result from `SwitchToAsync` as always meaning "rejected" — with `LockWhenScaling` enabled, `false` can also mean "accepted, but the move itself timed out and was undone."
