# Usage: HeartBeat

[**Back to README**](../../README.md) · See also: [Concepts](concepts.md)

## When to use

You want to periodically inspect a live item from the pool — e.g. a health check on a pooled connection — without a caller explicitly acquiring one for that purpose.

## Minimal example

```csharp
Random rnd = new();

var rb = await RingBuffer<int>.New("MyBuffer")
    .Logger(logger)
    .Factory((_) => Task.FromResult(rnd.Next(1, 10)))
    .HeartBeat(MyHeartBeat, pulse: TimeSpan.FromSeconds(10))
    .OnError((logger, ex) => logger?.LogError(ex, "RingBuffer background error"))
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);

static void MyHeartBeat(RingBufferValue<int> value)
{
    // inspect value.Current — e.g. a health check
    if (!IsHealthy(value.Current))
    {
        // Discard this item instead of returning it to the pool on dispose - a
        // replacement is created in its place. Do not dispose the value yourself here
        // (see "Do not dispose" below); Invalidate() just marks it for discard.
        value.Invalidate();
    }
}

static bool IsHealthy(int current) => true; // your real health check goes here
```

`OnError` is where an exception thrown by `MyHeartBeat` itself, or a heartbeat that blocks past its `pulse` budget, is routed — see "What happens internally" below. Without it (and without `Logger`), those failures are swallowed silently.

`HeartBeat` is available from every builder mode (`FixedCapacity`, `ElasticCapacity`, `AutoScaleAcquireFault`) — it is orthogonal to capacity mode.

## What happens internally

On every `pulse` interval (default 10 seconds — `RingBufferDefault.PulseHeartBeat`), the engine acquires one item from the pool the same way `AcquireAsync` would, and invokes your callback (via `Task.Run`, with a timeout equal to `pulse`) with the resulting `RingBufferValue<T>`. If no item is available for that tick, the pulse is skipped with a log message — there is no retry within the same interval.

What happens to the item afterwards differs by outcome: if the callback returns within `pulse`, the item is returned to the pool itself (subject to any `Invalidate()` the callback called). If the callback does **not** return within `pulse`, the slot is replaced immediately (a fresh item is created to take its place) — the original item is *not* returned to the pool, and its physical disposal is deferred until the orphaned callback (which cannot be forcibly cancelled and keeps running on its own thread) actually finishes, to avoid disposing it while it might still be in use. If `DisposeAsync()` on the buffer itself runs while a callback is still orphaned like this, it now waits for that deferred disposal to actually happen, bounded by `pulse` — see "Common errors" below.

**Do not dispose the `RingBufferValue<T>` passed to your callback — the engine owns that lifecycle for heartbeat items.** Unlike a caller-initiated `AcquireAsync`, the heartbeat pump disposes it for you after your callback returns.

Any exception the callback throws, and a callback that doesn't finish within `pulse`, are both caught internally and routed through the configured error handler (`OnError`) — neither crashes the pump nor propagates to any caller.

## Trade-offs / limitations

- A heartbeat acquisition competes with real callers for a pool slot, the same as any `AcquireAsync` — under a fully saturated pool, a heartbeat tick can find nothing available and skip silently (best-effort, not guaranteed to run every `pulse`).
- The pump awaits the callback (up to `pulse`) before scheduling the next tick — a slow callback delays subsequent pulses and, if it exceeds `pulse`, is abandoned and logged as a timeout. Keep it fast, or hand off long work to another task/queue from inside it. Abandoning it also means the slot is replaced (not just skipped) and the original item's disposal is deferred until the callback itself finishes — see "What happens internally" above.

## Common errors

- Calling `DisposeAsync`/anything that returns the item manually from inside the heartbeat callback — the engine already owns that item's return-to-pool step; doing it yourself risks a double-return.
- Assuming a heartbeat exception surfaces to whoever called `BuildWarmupAsync`/`AcquireAsync` — it doesn't; check `OnError`/your logger instead.
- Assuming the buffer's own `DisposeAsync()` returns immediately once its background pumps stop: if an orphaned callback (see "What happens internally") is still running when `DisposeAsync()` is called, it now waits up to `pulse` (the same budget the pump itself gives a heartbeat cycle) for that callback's deferred resource disposal to actually finish before giving up and returning anyway. This matters if you're tuning a host/orchestrator shutdown grace period around this buffer's disposal — budget for `pulse` on top of whatever else `DisposeAsync()` already does, including its own drain of every idle pooled item: each one's `Dispose()`/`DisposeAsync()` is separately bounded by this same `pulse` value too, independent of whether a `HeartBeat` callback is orphaned at all (see [Concepts](concepts.md)). If the callback is truly permanently stuck, `DisposeAsync()` still returns after that budget elapses (logged, not thrown) — the resource itself is disposed later if/when the callback ever finishes.
