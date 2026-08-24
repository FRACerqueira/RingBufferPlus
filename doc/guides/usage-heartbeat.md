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
    .OnError(ex => logger?.LogError(ex, "RingBuffer background error"))
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);

static bool MyHeartBeat(int value)
{
    // inspect value directly - e.g. a health check
    return IsHealthy(value);
}

static bool IsHealthy(int current) => true; // your real health check goes here
```

`OnError` is where an exception thrown by `MyHeartBeat` itself, or a heartbeat that blocks past its `pulse` budget, is routed — see "What happens internally" below. Without it (and without `Logger`), those failures are swallowed silently.

`HeartBeat` is available from every builder mode (`FixedCapacity`, `ElasticCapacity`) — it is orthogonal to capacity mode.

## What happens internally

On every `pulse` interval (default 10 seconds — `RingBufferDefault.PulseHeartBeat`), the engine acquires one item from the pool the same way `AcquireAsync` would, and invokes your callback (via `Task.Run`, with a timeout equal to `pulse`) with the item itself. If no item is available for that tick, the pulse is skipped with a log message — there is no retry within the same interval.

Since v6.0.0 (ADR007V03), `HeartBeat` takes a `Func<T, bool>`, not an `Action<RingBufferValue<T>>` — the framework owns acquiring and returning the item; your callback only decides its fate. Returning `true` returns the item to the pool normally; returning `false` discards it and creates a replacement in its place, through the exact same path a caller's own `Invalidate()` uses. There is no disposable object handed to you, so there is nothing to dispose or misuse.

If the callback does **not** return within `pulse`, there is no bool to interpret: the slot is replaced immediately (a fresh item is created to take its place) regardless of what the callback would eventually have returned — the original item is *not* returned to the pool, and its physical disposal is deferred until the orphaned callback (which cannot be forcibly cancelled and keeps running on its own thread) actually finishes, to avoid disposing it while it might still be in use. If `DisposeAsync()` on the buffer itself runs while a callback is still orphaned like this, it now waits for that deferred disposal to actually happen, bounded by `pulse` — see "Common errors" below.

Any exception the callback throws, and a callback that doesn't finish within `pulse`, are both caught internally and routed through the configured error handler (`OnError`) — neither crashes the pump nor propagates to any caller. A thrown exception does **not** discard the item (it is returned to the pool normally, same as `true`) — only an explicit `false` return does.

**A `false` verdict on an item whose own `Dispose()`/`DisposeAsync()` then hangs delays this pump by at most one `pulse`, not forever.** The engine's capacity bookkeeping is not blocked by this either way — the replacement is dispatched before that wait begins. The dispose itself is bounded by `pulse`: if it hasn't finished by then, it's deferred (same mechanism as the orphaned-callback case above) and the pump moves on to its next tick; `DisposeAsync()` on the buffer itself still waits for that deferred disposal, bounded the same way. This differs from a caller's own `Invalidate()` + `DisposeAsync()` on their own `RingBufferValue<T>`, which still awaits the item's dispose directly and unbounded — a hang there is genuinely local to that caller's own call, not something the framework can bound on their behalf.

## Trade-offs / limitations

- A heartbeat acquisition competes with real callers for a pool slot, the same as any `AcquireAsync` — under a fully saturated pool, a heartbeat tick can find nothing available and skip silently (best-effort, not guaranteed to run every `pulse`).
- The pump awaits the callback (up to `pulse`) before scheduling the next tick — a slow callback delays subsequent pulses and, if it exceeds `pulse`, is abandoned and logged as a timeout. Keep it fast, or hand off long work to another task/queue from inside it. Abandoning it also means the slot is replaced (not just skipped) and the original item's disposal is deferred until the callback itself finishes — see "What happens internally" above.
- Returning `false` for an item whose own dispose can hang (e.g. a stuck network connection) delays the pump by at most one `pulse` before the disposal is deferred and the pump moves on — see "What happens internally" above.

## Common errors

- Assuming a heartbeat exception surfaces to whoever called `BuildWarmupAsync`/`AcquireAsync` — it doesn't; check `OnError`/your logger instead.
- Assuming a thrown exception discards the item the same way returning `false` does — it doesn't; only an explicit `false` return does.
- Assuming the buffer's own `DisposeAsync()` returns immediately once its background pumps stop: if an orphaned callback (see "What happens internally") is still running when `DisposeAsync()` is called, it now waits up to `pulse` (the same budget the pump itself gives a heartbeat cycle) for that callback's deferred resource disposal to actually finish before giving up and returning anyway. This matters if you're tuning a host/orchestrator shutdown grace period around this buffer's disposal — budget for `pulse` on top of whatever else `DisposeAsync()` already does, including its own drain of every idle pooled item: each one's `Dispose()`/`DisposeAsync()` is separately bounded by this same `pulse` value too, independent of whether a `HeartBeat` callback is orphaned at all (see [Concepts](concepts.md)). If the callback is truly permanently stuck, `DisposeAsync()` still returns after that budget elapses (logged, not thrown) — the resource itself is disposed later if/when the callback ever finishes.
