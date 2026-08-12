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
    .FixedCapacity(3)
    .BuildWarmupAsync(cancellation);

static void MyHeartBeat(RingBufferValue<int> value)
{
    // inspect value.Current — e.g. a health check
}
```

`HeartBeat` is available from every builder mode (`FixedCapacity`, `ElasticCapacity`, `AutoScaleAcquireFault`) — it is orthogonal to capacity mode.

## What happens internally

On every `pulse` interval (default 10 seconds — `RingBufferDefault.PulseHeartBeat`), the engine acquires one item from the pool the same way `AcquireAsync` would, invokes your callback (via `Task.Run`, with a timeout equal to `pulse`) with the resulting `RingBufferValue<T>`, and then returns the item to the pool itself once the callback returns or times out. If no item is available for that tick, the pulse is skipped with a log message — there is no retry within the same interval.

**Do not dispose the `RingBufferValue<T>` passed to your callback — the engine owns that lifecycle for heartbeat items.** Unlike a caller-initiated `AcquireAsync`, the heartbeat pump disposes it for you after your callback returns.

Any exception the callback throws, and a callback that doesn't finish within `pulse`, are both caught internally and routed through the configured error handler (`OnError`) — neither crashes the pump nor propagates to any caller.

## Trade-offs / limitations

- A heartbeat acquisition competes with real callers for a pool slot, the same as any `AcquireAsync` — under a fully saturated pool, a heartbeat tick can find nothing available and skip silently (best-effort, not guaranteed to run every `pulse`).
- The pump awaits the callback (up to `pulse`) before scheduling the next tick — a slow callback delays subsequent pulses and, if it exceeds `pulse`, is abandoned and logged as a timeout. Keep it fast, or hand off long work to another task/queue from inside it.

## Common errors

- Calling `DisposeAsync`/anything that returns the item manually from inside the heartbeat callback — the engine already owns that item's return-to-pool step; doing it yourself risks a double-return.
- Assuming a heartbeat exception surfaces to whoever called `BuildWarmupAsync`/`AcquireAsync` — it doesn't; check `OnError`/your logger instead.
